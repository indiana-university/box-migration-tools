#Requires -Modules CCILogger, CCITaskTools, CCISecrets, SQLServer

<#

This script runs as a scheduled task to process post migration tasks. These tasks include:

    1. Bootstrap post migration tasks. This creates a new folder in the managed user's folder which will contain all of the migrated user data.

    2. All top level items owned by the migrated user will be moved to new folder in managed user account.

    3. All shared files will have their access updated to viewer for all collaborators.

    4. Finally, set the migrated user to viewer for all content in new managed folder with user's data.

#>

[CmdletBinding()]

param(
    [Parameter(Mandatory)]
    [string]$ConfigurationFilePath
)

# Propagate verbose flag if needed.

$IsVerbose = $false
if ($VerbosePreference -eq 'Continue') { $IsVerbose = $true }

# Import SkySync helper module.

Import-Module "$PSScriptRoot\SkySync.psm1"

# Load configuration.

try {
    Write-Verbose "Loading configuration file: $ConfigurationFilePath"
    $Configuration = Get-Content -Path $ConfigurationFilePath | ConvertFrom-Json
} catch {

    $subject = "SkySync [$($env:COMPUTERNAME + ':' + $env:USERDOMAIN)] : Error retrieving configuration file for $($PSCmdlet.MyInvocation.MyCommand)"
    $_ | Write-CCIEmailLog -ToEmailAddress 'ads-admin@iu.edu' -EmailSubject $subject -Verbose:$IsVerbose
    throw $_

}

# Retrieve SkySync database credentials and Box API key.

try {
    if (Test-Path $Configuration.CredentialsPath -ErrorAction Ignore) {
        Write-Verbose "Retrieving credentials from: $($Configuration.CredentialsPath)"
        $SkySyncCreds, $BoxAPICreds = Get-CCICredentialFromFile -Path $Configuration.CredentialsPath -ErrorAction Stop -Verbose:$IsVerbose
    } else { throw 'Path to credentials could not be found. : ' + $Configuration.CredentialsPath }
} catch {
    $subject = "SkySync [$($env:COMPUTERNAME + ':' + $env:USERDOMAIN)] : Error retrieving credentials for $($PSCmdlet.MyInvocation.MyCommand)"
    $_ | Write-CCIEmailLog -ToEmailAddress 'ads-admin@iu.edu' -EmailSubject $subject -Verbose:$IsVerbose
    throw $_
}

# Verify certain critical pieces of the configuration.

if (![int64]::TryParse($Configuration.BoxManagedUserID, [ref]$null)) {
    $subject = "SkySync [$($env:COMPUTERNAME + ':' + $env:USERDOMAIN)] : Error retrieving configuration file for $($PSCmdlet.MyInvocation.MyCommand). No valid BoxManagedUserID found."
    ('BoxManagedUserID : ' + $Configuration.BoxManagedUserID) | Write-CCIEmailLog -ToEmailAddress 'ads-admin@iu.edu' -EmailSubject $subject -Verbose:$IsVerbose
    throw $_
}

# Setup script state object. This is mostly just to provide diagnostics for error emails.

$ScriptState = [ordered]@{
    ScriptName       = $PSCmdlet.MyInvocation.MyCommand
    TaskServer       = $env:COMPUTERNAME
    TaskServerDomain = $env:USERDOMAIN
}

#=====================================================================================================================================================

## ~~ Get Next Job ~~ ##

#region GetNextJob

# Get the next job to be processed.

try {

    $splat = @{
        Server          = $Configuration.WebAppSQLServer
        Database        = $Configuration.WebAppDatabase
        StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.StartProcessingNextJob')
        TimeoutSec      = 90
        ErrorAction     = 'Stop'
        Verbose         = $IsVerbose
    }

    Write-Verbose "Retrieving next job to process from server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"
    $TransferJob = $null
    $TransferJob = Invoke-CCIStoredProcedure @splat

} catch {

    $subject = 'SkySync : Error getting next job to be processed for post-migration tasks'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}

# Quit here if there is no jobs to process.

if ($null -eq $TransferJob) {
    Write-Verbose 'No jobs to process.'
    return
}

# Update ScriptState.

$ScriptState.JobID     = $TransferJob.JobID
$ScriptState.UserEmail = $TransferJob.AccountEmail

#endregion GetNextJob

#=====================================================================================================================================================

## ~~ Bootstrap ~~ ##

#region Bootstrap

# Only need to run Bootstrap task if there is no recorded UserID or ManagedFolderID.

# These variables will be used throughout the rest of the script.
$UserID          = $null
$ManagedFolderID = $null

if ([System.DBNull]::Value -eq $TransferJob.UserID -or [System.DBNull]::Value -eq $TransferJob.ManagedFolderID) {

    $CorrelationID   = [string](New-Guid)

    # Invoke the bootstrap process.

    try {

        $splat = @{
            AccountEmail        = $TransferJob.AccountEmail
            ManagedUserID       = $Configuration.BoxManagedUserID
            PostMigrationAPIUri = $Configuration.BoxPostMigrationAPI
            SharedSecret        = $BoxAPICreds
            CorrelationID       = $CorrelationID
            ErrorAction         = 'Stop'
            Verbose             = $IsVerbose
        }

        $BootstrapResponse = $null
        $BootstrapResponse = Invoke-BoxBootstrap @splat

    } catch {

        $subject = 'SkySync : Error bootstrapping user box folder'
        $errorResponse = @{
            StatusCode    = $_.Exception.Response.StatusCode.value__
            StatusReason  = $_.Exception.Response.StatusCode
            Response      = $_.ErrorDetails.Message
            CorrelationID = $CorrelationID
        }
        $Configuration, [PSCustomObject]$ScriptState, [PSCustomObject]$errorResponse, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
        throw $_

    }

    # Verify we got our managed folder ID back.

    if ($null -eq $BootstrapResponse.Response.Content -or $BootstrapResponse.Response.Content.Length -eq 0) {

        $subject = 'SkySync : Error bootstrapping user box folder : No Box response returned in body'
        $errorResponse = @{
            StatusCode        = $BootstrapResponse.Response.StatusCode
            StatusDescription = $BootstrapResponse.Response.StatusDescription
            Response          = $BootstrapResponse.Response.Content
            CorrelationID     = $CorrelationID
        }
        $Configuration, [PSCustomObject]$ScriptState, [PSCustomObject]$errorResponse | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
        throw $subject

    }

    # Update these two variables needed through out the rest of the script.

    $UserID          = ($BootstrapResponse.Response.Content | ConvertFrom-Json).userId
    $ManagedFolderID = ($BootstrapResponse.Response.Content | ConvertFrom-Json).managedFolderId

    # Verify we got integers for our managed folder ID and UserID.

    if ([int64]::TryParse($ManagedFolderID, [ref]$null) -and [int64]::TryParse($UserID, [ref]$null)) {
        $UserID          = [int64]$UserID
        $ManagedFolderID = [int64]$ManagedFolderID
    } else {

        $subject = "SkySync : Error bootstrapping user box folder : Non-integer ID returned in response"
        $errorResponse = @{
            StatusCode        = $BootstrapResponse.Response.StatusCode
            StatusDescription = $BootstrapResponse.Response.StatusDescription
            Response          = $BootstrapResponse.Response.Content
            CorrelationID     = $CorrelationID
        }
        $Configuration, [PSCustomObject]$ScriptState, [PSCustomObject]$errorResponse, [PSCustomObject]@{
            ManagedFolderIDWithSurroundingBrackets = ">$ManagedFolderID<"
            UserIDWithSurroundingBrackets = ">$UserID<"
        } | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
        throw $subject

    }

    # Update user ManagedFolderID in database. Also include information about the request and response of the Box Post-Migration API.

    try {

        Write-Verbose "Inserting user bootstrap results and received IDs into server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"

        $splat = @{
            Server          = $Configuration.WebAppSQLServer
            Database        = $Configuration.WebAppDatabase
            StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.InsertUserBootstrap')
            TimeoutSec      = 90
            ErrorAction     = 'Stop'
            Verbose         = $IsVerbose
        }

        $userObj = @{
            UserID                 = $UserID
            AccountEmail           = $TransferJob.AccountEmail
            ManagedFolderID        = $ManagedFolderID
            BootstrapCorrelationID = $CorrelationID
            BootstrapRequestBody   = ($BootstrapResponse.RequestBody | ConvertTo-Json)
            BootstrapResponse      = @{
                StatusCode        = $BootstrapResponse.Response.StatusCode
                StatusDescription = $BootstrapResponse.Response.StatusDescription
                Content           = $BootstrapResponse.Response.Content
            } | ConvertTo-Json
        }

        $userObj | Invoke-CCIStoredProcedure @splat

    } catch {

        $subject = 'SkySync : Error inserting user bootstrap details into database'
        $errorResponse = @{
            UserID          = $UserID
            ManagedFolderID = $ManagedFolderID
        }
        $Configuration, [PSCustomObject]$ScriptState, [PSCustomObject]$errorResponse, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
        throw $_

    }

} else {
    Write-Verbose "User, $($TransferJob.AccountEmail), already bootstrapped."
    $UserID              = $TransferJob.UserID
    $ManagedFolderID     = $TransferJob.ManagedFolderID
    $ScriptState.IsRetry = $true
}

# Verify UserID and ManagedFolderID look valid, whether that comes from the database or from the Box API call.

$ScriptState.UserID          = $UserID
$ScriptState.ManagedFolderID = $ManagedFolderID

if (![int64]::TryParse($UserID, [ref]$null) -or ![int64]::TryParse($ManagedFolderID, [ref]$null)) {
    $subject = 'SkySync : Error bootstrapping user box folder : Invalid UserID or ManagedFolderID'
    $errorResponse = @{
        StatusCode        = $TransferJob.StatusCode
        StatusDescription = $TransferJob.StatusDescription
        Content           = $TransferJob.Content
    }
    $Configuration, [PSCustomObject]$ScriptState, [PSCustomObject]$errorResponse | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $subject
}

#endregion Bootstrap

#=====================================================================================================================================================

## ~~ UpdateCollaborations ~~ ##

#region UpdateCollaborations

# Get all TransferPermissions associated with the current job.

try {

    Write-Verbose "Getting transfer permissions from server:database: $($Configuration.SkySyncSQLServer):$($Configuration.SkySyncDatabase)"

    $qTransferPermissions = "
SELECT
    DISTINCT(TransferItems.SourceID) AS SourceItemID,
    TransferPermissions.TransferItemID,
    TransferItems.SourceType
FROM [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].TransferPermissions
    LEFT JOIN [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].TransferItems
        ON TransferItems.ID = TransferPermissions.TransferItemID
WHERE TransferID = $($TransferJob.JobID)
"

    $TransferPermissions = @()
    [array]$TransferPermissions = Invoke-Sqlcmd -Credential $SkySyncCreds -ServerInstance $Configuration.SkySyncSQLServer -Query $qTransferPermissions -ErrorAction Stop -Verbose:$IsVerbose

} catch {

    $subject = 'SkySync : Error gathering transfer permissions'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}

# This will be used to store the error messages, and BOX API responses for each failed item. We will report them at the end.

$ErrorMessages = @()

# Get any previously successful operations so that we do not repeat them. This is just to save time, repeating them is not a problem.

$PreviousSuccessfulOps = @{}

if ($ScriptState.IsRetry) {

    try {

        Write-Verbose "Retrieving any previously successful transfer permission operations from server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"

        $splat = @{
            Server          = $Configuration.WebAppSQLServer
            Database        = $Configuration.WebAppDatabase
            StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.GetSuccessfulTransferPermissions')
            TimeoutSec      = 90
            ErrorAction     = 'Stop'
            Verbose         = $IsVerbose
        }

        @{ JobID = $TransferJob.JobID } | Invoke-CCIStoredProcedure @splat | ForEach-Object { $PreviousSuccessfulOps[$_.TransferItemID] = $true }

    } catch {

        $subject = 'Error retrieving previously successful transfer permission operations from database'
        $ErrorMessages += "<h2>$subject</h2>", $_

        $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
        throw $_

    }

}

# Process available TransferPermissions.

if ($TransferPermissions.Count -gt 0) {

    foreach ($Perm in $TransferPermissions) {

        if ($ScriptState.IsRetry -and $PreviousSuccessfulOps[$Perm.TransferItemID]) {
            Write-Verbose "Skipping transfer permission as it was successful on a previous attempt: TransferItemID = $($Perm.TransferItemID) | UserID = $($UserID)"
            continue
        }

        $CorrelationID = [string](New-Guid)

        if ([System.DBNull]::Value -ne $Perm.SourceItemID -and [int64]::TryParse($Perm.SourceItemID, [ref]$null)) {

            # Run UpdateCollaborations Box Post-Migration API endpoint on each top shared TransferPermissions item.

            try {

                $splat = @{
                    UserID              = $UserID
                    ItemID              = $Perm.SourceItemID
                    ItemType            = $(if ($Perm.SourceType -eq 'f') { 'File' } else { 'Folder' })
                    ManagedUserID       = $Configuration.BoxManagedUserID
                    PostMigrationAPIUri = $Configuration.BoxPostMigrationAPI
                    SharedSecret        = $BoxAPICreds
                    CorrelationID       = $CorrelationID
                    ErrorAction         = 'Stop'
                    Verbose             = $IsVerbose
                }

                $UpdateCollabResponse = $null
                $UpdateCollabResponse = Invoke-BoxUpdateCollaboration @splat

            } catch {

                # We need to record this item failure. So we will fake the response objects from above for the database entry.

                $UpdateCollabResponse = [PSCustomObject]@{
                    RequestBody = 'Request Failed : Update Collaborations Failure'
                    Response    = @{
                        StatusCode        = $_.Exception.Response.StatusCode.value__
                        StatusDescription = $_.Exception.Response.StatusCode
                        Content           = $_.ErrorDetails.Message
                    }
                }

                $subject = 'Error updating collaborations during post-migration tasks'
                $errorResponse = [ordered]@{
                    TransferItemID = $Perm.TransferItemID
                    SourceItemID   = $Perm.SourceItemID
                    StatusCode     = $_.Exception.Response.StatusCode.value__
                    StatusReason   = $_.Exception.Response.StatusCode
                    Response       = $_.ErrorDetails.Message
                    CorrelationID  = $CorrelationID
                }
                $ErrorMessages += "<h2>$subject</h2>", [PSCustomObject]$errorResponse, $_

                # If this catch block is triggered it could be an transient Box API issue. We will simply record the error for later and move on.

                Write-Error -Exception $_.Exception -Message ($subject + ' : Error Message = ' + $_.ErrorDetails.Message)

            }

        } else {

            # No source item ID found so there is no way to update collaborations on this item. Record the unexpected skip of the item permissions.

            $UpdateCollabResponse = [PSCustomObject]@{
                RequestBody = 'No Request Sent : Update Collaborations Skipped'
                Response    = @{
                    StatusCode        = 0
                    StatusDescription = 'No Response : Update Collaborations Skipped'
                    Content           = 'No request sent to the Box API endpoint. No source item ID was found and therefore could not update collaborations.'
                }
            }
            $Perm = [PSCustomObject]@{
                TransferItemID = $Perm.TransferItemID
                SourceItemID   = -1
            }

        }

        # Insert TransferPermission details and API response to database.

        try {

            Write-Verbose "Inserting transfer permission update-collaborators results into server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"
    
            $splat = @{
                Server          = $Configuration.WebAppSQLServer
                Database        = $Configuration.WebAppDatabase
                StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.InsertTransferPermission')
                TimeoutSec      = 90
                ErrorAction     = 'Stop'
                Verbose         = $IsVerbose
            }
    
            $transferPermObj = @{
                JobID                     = $TransferJob.JobID
                TransferItemID            = $Perm.TransferItemID
                SourceItemID              = $Perm.SourceItemID
                UpdateCollabCorrelationID = $CorrelationID
                UpdateCollabRequestBody   = ($UpdateCollabResponse.RequestBody | ConvertTo-Json)
                UpdateCollabResponse      = @{
                    StatusCode        = $UpdateCollabResponse.Response.StatusCode
                    StatusDescription = $UpdateCollabResponse.Response.StatusDescription
                    Content           = $UpdateCollabResponse.Response.Content
                } | ConvertTo-Json
            }
    
            $transferPermObj | Invoke-CCIStoredProcedure @splat
    
        } catch {
    
            $subject = 'Error inserting transfer permission results into database'
            $errorResponse = [ordered]@{
                TransferItemID = $Perm.TransferItemID
                SourceItemID   = $Perm.SourceItemID
            }
            $ErrorMessages += "<h2>$subject</h2>", [PSCustomObject]$errorResponse, $_

            # If this catch block is triggered then there is either a bug in the code or a server issue. Either way we want to stop and report.
            # The $ErrorMessages block may contain other Box API errors from above as well.

            $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
            throw $_
    
        }

    }

} else {

    Write-Verbose ('No transfer permissions to process for JobID: ' + $TransferJob.JobID)

}

# If there were any non-terminating errors above, report them now.

if ($ErrorMessages.Count -gt 0) {
    $subject = 'Errors occured during post-migration UpdateCollaborations tasks'
    "<h1>$subject</h1>", $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
}

#endregion UpdateCollaborations

#=====================================================================================================================================================

## ~~ MoveItems ~~ ##

#region MoveItem

# Get all TransferItems associated with the current job.

try {

    Write-Verbose "Getting transfer items from server:database: $($Configuration.SkySyncSQLServer):$($Configuration.SkySyncDatabase)"

    # SkipReasons: (Best Guess)
    #
    # These are excluded as they either include files that shouldn't be moved (ex. owned by someone else) or can't be moved (ex. no source id).
    #
    # 8    = Destination exists but source does not and policy is set to ignore destination files.
    # 512  = Skipped due to applied filter. Likely an item owned by another user and shared with this one.
    #

    if (($TransferJob.DisplayName -like '*-> SPO*') -or ($TransferJob.DisplayName -like '*-> GSD*')) {
        $PathDepth = 0
    } else {
        $PathDepth = 1
    }

    $qTransferItems = "
SELECT
    ID AS TransferItemID,
    TransferID AS JobID,
    SourceID AS SourceItemID,
    SourceType,
    SkipReason
FROM [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].TransferItems
WHERE TransferID = $($TransferJob.JobID)
    AND (SkipReason IS NULL OR (SkipReason != 512 AND SkipReason != 8))
    AND PathDepth = $PathDepth
"

    $TransferItems = @()
    [array]$TransferItems = Invoke-Sqlcmd -Credential $SkySyncCreds -ServerInstance $Configuration.SkySyncSQLServer -Query $qTransferItems -ErrorAction Stop -Verbose:$IsVerbose

} catch {

    $subject = 'SkySync : Error gathering transfer items'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}

# This will be used to store the error messages, and BOX API responses for each failed item. We will report them at the end.

$ErrorMessages = @()

# Get any previously successful operations so that we do not repeat them. This is just to save time, repeating them is not a problem.

$PreviousSuccessfulOps = @{}

if ($ScriptState.IsRetry) {

    try {

        Write-Verbose "Retrieving any previously successful transfer items operations from server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"

        $splat = @{
            Server          = $Configuration.WebAppSQLServer
            Database        = $Configuration.WebAppDatabase
            StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.GetSuccessfulTransferItems')
            TimeoutSec      = 90
            ErrorAction     = 'Stop'
            Verbose         = $IsVerbose
        }

        @{ JobID = $TransferJob.JobID } | Invoke-CCIStoredProcedure @splat | ForEach-Object { $PreviousSuccessfulOps[$_.TransferItemID] = $true }

    } catch {

        $subject = 'Error retrieving previously successful transfer item operations from database'
        $ErrorMessages += "<h2>$subject</h2>", $_

        $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
        throw $_

    }

}

# Process available TransferItems.

if ($TransferItems.Count -gt 0) {

    foreach ($Item In $TransferItems) {

        if ($ScriptState.IsRetry -and $PreviousSuccessfulOps[$Item.TransferItemID]) {
            Write-Verbose "Skipping transfer item as it was successful on a previous attempt: TransferItemID = $($Item.TransferItemID) | UserID = $($UserID)"
            continue
        }

        $CorrelationID = [string](New-Guid)

        if ([System.DBNull]::Value -ne $Item.SourceItemID -and [int64]::TryParse($Item.SourceItemID, [ref]$null)) {

            # Run MoveItem Box Post-Migration API endpoint on each top level TransferItem.

            try {

                $splat = @{
                    UserID              = $UserID
                    ItemID              = $Item.SourceItemID
                    ItemType            = $(if ($Item.SourceType -eq 'f') { 'File' } else { 'Folder' })
                    ManagedFolderID     = $ManagedFolderID
                    PostMigrationAPIUri = $Configuration.BoxPostMigrationAPI
                    SharedSecret        = $BoxAPICreds
                    CorrelationID       = $CorrelationID
                    ErrorAction         = 'Stop'
                    Verbose             = $IsVerbose
                }

                $MoveItemResponse = $null
                $MoveItemResponse = Invoke-BoxMoveItem @splat

            } catch {

                # We need to record this item failure. So we will fake the response objects from above for the database entry.

                $MoveItemResponse = [PSCustomObject]@{
                    RequestBody = 'Request Failed : Move Item Failure'
                    Response    = @{
                        StatusCode        = $_.Exception.Response.StatusCode.value__
                        StatusDescription = $_.Exception.Response.StatusCode
                        Content           = $_.ErrorDetails.Message
                    }
                }

                # Save the error response and messages for later.

                $subject = 'Error moving items during post-migration tasks'
                $errorResponse = [ordered]@{
                    TransferItemID = $Item.TransferItemID
                    SourceItemID   = $Item.SourceItemID
                    StatusCode     = $_.Exception.Response.StatusCode.value__
                    StatusReason   = $_.Exception.Response.StatusCode
                    Response       = $_.ErrorDetails.Message
                    CorrelationID  = $CorrelationID
                }
                $ErrorMessages += "<h2>$subject</h2>", [PSCustomObject]$errorResponse, $_

                # If this catch block is triggered it could be an transient Box API issue. We will simply record the error for later and move on.

                Write-Error -Exception $_.Exception -Message ($subject + ' : Error Message = ' + $_.ErrorDetails.Message)

            }

        } else {

            # No source item ID found so there is no way to move this item. Record the unexpected skip of the item.

            $MoveItemResponse = [PSCustomObject]@{
                RequestBody = 'No Request Sent : Move Item Skipped'
                Response    = @{
                    StatusCode        = 0
                    StatusDescription = 'No Response : Move Item Skipped'
                    Content           = "No request sent to the Box API endpoint. No source item ID was found and therefore could not be moved. SkipReason = $($Item.SkipReason)"
                }
            }
            $Item = [PSCustomObject]@{
                TransferItemID = $Item.TransferItemID
                SourceItemID   = -1
            }

        }

        # Insert TransferItem details and API response to database.

        try {

            Write-Verbose "Inserting transfer item move-item results into server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"
    
            $splat = @{
                Server          = $Configuration.WebAppSQLServer
                Database        = $Configuration.WebAppDatabase
                StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.InsertTransferItem')
                TimeoutSec      = 90
                ErrorAction     = 'Stop'
                Verbose         = $IsVerbose
            }
    
            $transferItemObj = @{
                TransferItemID        = $Item.TransferItemID
                JobID                 = $TransferJob.JobID
                SourceItemID          = $Item.SourceItemID
                MoveItemCorrelationID = $CorrelationID
                MoveItemRequestBody   = ($MoveItemResponse.RequestBody | ConvertTo-Json)
                MoveItemResponse      = @{
                    StatusCode        = $MoveItemResponse.Response.StatusCode
                    StatusDescription = $MoveItemResponse.Response.StatusDescription
                    Content           = $MoveItemResponse.Response.Content
                } | ConvertTo-Json
            }
    
            $transferItemObj | Invoke-CCIStoredProcedure @splat
    
        } catch {
    
            $subject = 'Error inserting transfer item into database'
            $errorResponse = [ordered]@{
                TransferItemID = $Item.TransferItemID
                SourceItemID   = $Item.SourceItemID
            }
            $ErrorMessages += "<h2>$subject</h2>", [PSCustomObject]$errorResponse, $_

            # If this catch block is triggered then there is either a bug in the code or a server issue. Either way we want to stop and report.
            # The $ErrorMessages block may contain other Box API errors from above as well.

            $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
            throw $_
    
        }

    }

} else {

    Write-Verbose ('No transfer items to process for JobID: ' + $TransferJob.JobID)

}

# If there were any non-terminating errors above, report them now.

if ($ErrorMessages.Count -gt 0) {
    $subject = 'Errors occured during post-migration MoveItem tasks'
    "<h1>$subject</h1>", $Configuration, [PSCustomObject]$ScriptState, $ErrorMessages | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
}

#endregion MoveItem

#=====================================================================================================================================================

## ~~ CleanUp ~~ ##

#region CleanUp

# Run CleanUp Box Post-Migration API endpoint on user managed folder.

if (-not $TransferJob.SkipCleanUpTasks) {

    $ErrorMessages = @()

    $CorrelationID = [string](New-Guid)

    try {

        $splat = @{
            UserID              = $UserID
            ManagedFolderID     = $ManagedFolderID
            ManagedUserID       = $Configuration.BoxManagedUserID
            PostMigrationAPIUri = $Configuration.BoxPostMigrationAPI
            SharedSecret        = $BoxAPICreds
            CorrelationID       = $CorrelationID
            ErrorAction         = 'Stop'
            Verbose             = $IsVerbose
        }

        $CleanUpResponse = $null
        $CleanUpResponse = Invoke-BoxCleanUp @splat

    } catch {

        $CleanUpResponse = [PSCustomObject]@{
            RequestBody = 'Request Failed : Clean Up Failure'
            Response    = @{
                StatusCode        = $_.Exception.Response.StatusCode.value__
                StatusDescription = $_.Exception.Response.StatusCode
                Content           = $_.ErrorDetails.Message
            }
        }

        $subject = 'Error performing cleanup process during post-migration tasks'
        $errorResponse = [ordered]@{
            StatusCode    = $_.Exception.Response.StatusCode.value__
            StatusReason  = $_.Exception.Response.StatusCode
            Response      = $_.ErrorDetails.Message
            CorrelationID = $CorrelationID
        }
        $ErrorMessages += "<h2>$subject</h2>", [PSCustomObject]$errorResponse, $_

        # If this catch block is triggered then the BOX API CleanUp process failed, we will report (email) the error after recording it in the database.

        Write-Error -Exception $_.Exception -Message ($subject + ' : Error Message = ' + $_.ErrorDetails.Message)

    }

    # Insert CleanUp details and API response to database.

    try {

        Write-Verbose "Updating cleanup results into server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"

        $splat = @{
            Server          = $Configuration.WebAppSQLServer
            Database        = $Configuration.WebAppDatabase
            StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.UpdateUserCleanUp')
            TimeoutSec      = 90
            ErrorAction     = 'Stop'
            Verbose         = $IsVerbose
        }

        $cleanUpObj = @{
            AccountEmail         = $TransferJob.AccountEmail
            CleanUpCorrelationID = $CorrelationID
            CleanUpRequestBody   = ($CleanUpResponse.RequestBody | ConvertTo-Json)
            CleanUpResponse      = @{
                StatusCode        = $CleanUpResponse.Response.StatusCode
                StatusDescription = $CleanUpResponse.Response.StatusDescription
                Content           = $CleanUpResponse.Response.Content
            } | ConvertTo-Json
        }

        $cleanUpObj | Invoke-CCIStoredProcedure @splat

    } catch {

        $subject = 'Error inserting cleanup results into database'
        $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
        throw $_

    }

}

#endregion CleanUp

#=====================================================================================================================================================

## ~~ FinishJob ~~ ##

#region FinishJob

try {

    Write-Verbose "Marking job as complete in server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"

    $splat = @{
        Server          = $Configuration.WebAppSQLServer
        Database        = $Configuration.WebAppDatabase
        StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.FinishJob')
        TimeoutSec      = 90
        ErrorAction     = 'Stop'
        Verbose         = $IsVerbose
    }

    $finishJobObj = @{
        JobID = $TransferJob.JobID
    }

    $IsRetryJob = $finishJobObj | Invoke-CCIStoredProcedure @splat

} catch {

    $subject = 'Error marking job as complete in database'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject "SkySync : $subject" -Digest -Verbose:$IsVerbose
    throw $_

}

#endregion FinishJob
