#Requires -Module ActiveDirectory

#=====================================================================================================================================================

#region Module Variables

$script:PostMigrationAPIUri = $null
$script:SharedSecret = $null

#endregion Module Variables

#=====================================================================================================================================================

#region Initialize-BoxConfiguration

<#

    .SYNOPSIS

        Initializes this module with the provided Box Post-Migration API configuration settings.

#>
function Initialize-BoxConfiguration {

    [CmdletBinding()]

    param(

        # The URI to the Box Post-Migration API service.
        [Parameter(Mandatory, HelpMessage = 'Enter the Box Post-Migration API uri. Do not include the specific API command endpoint.')]
        [ValidateNotNullOrEmpty()]
        [string]$PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [Parameter(Mandatory, HelpMessage = 'Enter the share secret for the Box Post-Migration API. No username required.')]
        [ValidateNotNull()]
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret

    )

    end {

        $script:PostMigrationAPIUri = ($PostMigrationAPIUri -replace '/$','') + '/'
        $script:SharedSecret        = $SharedSecret

    }

}

#endregion Initialize-BoxConfiguration

#=====================================================================================================================================================

#region Invoke-BoxRequest

<#

    .SYNOPSIS

        Sends a request to the given Box Post-Migration API endpoint using the given parameters.

    .DESCRIPTION

        Sends a request to the given Box Post-Migration API endpoint using the given parameters.

        While this command can be used to send requests to the API endpoint manually, it is really intended to be used as a helper function for the
        other functions provided by this module.

    .OUTPUTS

        Returns the HTTP response from the Box API.

#>
function Invoke-BoxRequest {

    [CmdletBinding()]

    param(

        # The Box Post-Migration API endpoint to send the request to.
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [ArgumentCompleter( {
            param (
                $CommandName,
                $ParameterName,
                $WordToComplete,
                $CommandAst,
                $FakeBoundParameters
            )
            return @('Bootstrap', 'MoveItem', 'UpdateCollaborations', 'Cleanup') | Where-Object { $_ -like "$WordToComplete*" }
        })]
        [string]$APIEndpoint,

        # A hashtable containing parameter/value pairs. These will be sent as JSON in the body of the HTTP request.
        [Parameter(ValueFromPipeline)]
        [hashtable]$Parameters,

        # The HTTP method to use for the API call. This is typically always POST for the Box Post-Migration API.
        [ValidateSet('GET', 'POST')]
        [string]$Method = 'POST',

        # Correlation ID used to track events with the API logs. If one is not provide a new GUID will be used.
        [string]$CorrelationID = (New-Guid),

        # The URI to the Box Post-Migration API service.
        [string]$PostMigrationAPIUri = $script:PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret = $script:SharedSecret,

        # The number of times to retry a request when certain error types are returned. Only a few error conditions are handled by this function,
        # such as rate limiting 429 errors.
        [int]$Retry = 8,

        # The number of seconds to wait on a Box API call. Default is 300 seconds (5 minutes).
        [int]$Timeout = 300

    )

    begin {

        # If these parameters were not given and the defaults have not be initialized, prompt the user and initialize them now.

        if ($null -eq $PostMigrationAPIUri -or $PostMigrationAPIUri.Length -eq 0 -or $null -eq $SharedSecret) {
            Initialize-BoxConfiguration -ErrorAction Stop
            $PostMigrationAPIUri = $script:PostMigrationAPIUri
            $SharedSecret        = $script:SharedSecret
        }

    }

    process {

        $uri = ($PostMigrationAPIUri -replace '/$','') + '/' + ($APIEndpoint -replace '^/|/$','') + '?code=' + $SharedSecret.GetNetworkCredential().Password

        $headers = @{ CorrelationID = $CorrelationID }

        $body = $Parameters | ConvertTo-Json

        Write-Verbose ($MyInvocation.MyCommand.Name + ' : Request Body = ' + $body)
        Write-Verbose ($MyInvocation.MyCommand.Name + ' : Headers = ' + ($headers | ConvertTo-Json))

        do {

            try {

                $resp = $null
                $resp = Invoke-WebRequest -Method $Method -Uri $uri -Body $body -Headers $headers -TimeoutSec $Timeout -UseBasicParsing -ErrorAction Stop

            } catch {

                if ($RetryCount -le $Retry) {

                    if ($_.Exception.Response.StatusCode -eq 429) {
                        $sleepTime = ([math]::Pow(2, $RetryCount))
                        Write-Verbose "Error 429 : Rate limiting. Sleeping for $sleepTime seconds..."
                        Start-Sleep -Seconds $sleepTime
                    } elseif ($_.Exception.Response.StatusCode -eq 500 -and $_.Exception.Message -match 'Timeout') {
                        $sleepTime = 2
                        Write-Verbose "Error 500 : Server side timeout. Sleeping for $sleepTime seconds..."
                        Start-Sleep -Seconds $sleepTime
                    } elseif ($_.Exception.Message -match 'The operation has timed out') {
                        $sleepTime = 2
                        Write-Verbose "Error : Client side timeout after $Timeout. Sleeping for $sleepTime seconds..."
                        Start-Sleep -Seconds $sleepTime
                    } else {
                        throw  # Rethrow other errors.
                    }

                } else {
                    throw  # Rethrow errors if Retry limit is reached.
                }

            }

        } until($null -ne $resp -or $RetryCount++ -gt $Retry)

        # Return response.

        $resp

    }

}

#endregion Invoke-BoxRequest

#=====================================================================================================================================================

#region Invoke-BoxBootstrap

<#

    .SYNOPSIS

        Prepare a managed folder to receive items owned by a migrated user. This will be called once per migrated user.

    .OUTPUTS

        Returns an object that contains the custom HTTP headers, HTTP request body, and HTTP response from the BootStrap endpoint of the Box
        Post-Migration API.

            Headers     - Contains the CorrelationID used during the request.
            RequestBody - Contains the parameters sent in the request body to the Box Post-Migration API endpoint.
            Response    - Returns the actual WebResponseObject from the API endpoint. The contents should contain a JSON string representing the Box
                            User ID of the migrated user and the Box ID of the folder that was created during the bootstrap process.

    .EXAMPLE

        Initialize migrated user's managed folder.

            Initialize-BoxConfiguration -PostMigrationAPIUri https://box-migration-test.azurewebsites.net/api/' -SharedSecret $(Get-Credential -Username 'Box Post-Migration API Shared Secret')
            $BootstrapResponse = Invoke-BoxBootstrap -AccountEmail username@indiana.edu -ManagedUserID 0987654321

            $CorrelationID   = $BootstrapResponse.Headers.CorrelationID
            $RequestBody     = $BootstrapResponse.RequestBody | ConvertFrom-Json
            $UserID          = ($BootstrapResponse.Response.Content | ConvertFrom-Json).userId
            $ManagedFolderID = ($BootstrapResponse.Response.Content | ConvertFrom-Json).managedFolderId

    .EXAMPLE

        Initialize migrated user's managed folder.

            $splat = @{
                AccountEmail        = username@indiana.edu  # Box User login of migrated user.
                ManagedUserID       = 0987654321  # Box User ID of UITS managed service account for long term storage.
                PostMigrationAPIUri = 'https://box-migration-test.azurewebsites.net/api/'
                SharedSecret        = $(Get-Credential -Username 'Box Post-Migration API Shared Secret')
            }
            $BootstrapResponse = Invoke-BoxBootstrap @splat

            $CorrelationID   = $BootstrapResponse.Headers.CorrelationID
            $RequestBody     = $BootstrapResponse.RequestBody | ConvertFrom-Json
            $UserID          = ($BootstrapResponse.Response.Content | ConvertFrom-Json).userId
            $ManagedFolderID = ($BootstrapResponse.Response.Content | ConvertFrom-Json).managedFolderId

#>
function Invoke-BoxBootstrap {

    [CmdletBinding()]

    param(

        # The Box login email address of the user to be migrated.
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string]$AccountEmail,

        # The Box user ID of the managed user who will hold the migrated data in Box.
        #
        # When a user's data is migrated, the copy in Box will have its ownership changed to this managed user.
        [Parameter(Mandatory)]
        [string]$ManagedUserID,

        # The URI to the Box Post-Migration API service.
        [string]$PostMigrationAPIUri = $script:PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret = $script:SharedSecret,

        # Correlation ID used to track events with the API logs. If one is not provide a new GUID will be used.
        [string]$CorrelationID = (New-Guid)

    )

    begin {

        # If these parameters were not given and the defaults have not be initialized, prompt the user and initialize them now.

        if ($null -eq $PostMigrationAPIUri -or $PostMigrationAPIUri.Length -eq 0 -or $null -eq $SharedSecret) {
            Initialize-BoxConfiguration -ErrorAction Stop
            $PostMigrationAPIUri = $script:PostMigrationAPIUri
            $SharedSecret        = $script:SharedSecret
        }

    }

    process {

        Write-Verbose ('=' * 80)
        Write-Verbose ("$($MyInvocation.MyCommand.Name) : UserLogin = $UserLogin : CorrelationID = $CorrelationID")

        $body = @{
            UserLogin     = $AccountEmail
            ManagedUserId = $ManagedUserID
        }

        $splat = @{
            APIEndpoint         = 'Bootstrap'
            Parameters          = $body
            CorrelationID       = $CorrelationID
            PostMigrationAPIUri = $PostMigrationAPIUri
            SharedSecret        = $SharedSecret
            ErrorAction         = 'Stop'
        }

        $resp = Invoke-BoxRequest @splat

        # Return both the request body and response.

        [PSCustomObject]@{
            RequestBody = $body
            Response    = $resp
        }

    }

}

#endregion Invoke-BoxBootstrap

#=====================================================================================================================================================

#region Invoke-BoxMoveItem

<#

    .SYNOPSIS

        Move a single top-level file or folder from a migrated account to a long-term storage account. This will (likely) be called many times per
        migrated user.

    .OUTPUTS

        Returns an object that contains the custom HTTP headers, HTTP request body, and HTTP response from the MoveItem endpoint of the Box
        Post-Migration API.

            Headers     - Contains the CorrelationID used during the request.
            RequestBody - Contains the parameters sent in the request body to the Box Post-Migration API endpoint.
            Response    - Returns the actual WebResponseObject from the API endpoint. The contents should be empty and its status code should be 200.

    .EXAMPLE

        Move item to user's managed folder created in the bootstrap process.

            $splat = @{
                UserID              = 1234567890  # Box User ID of migrated user.
                ItemID              = 1234509876  # The file/folder ID of the item being moved.
                ItemType            = File  # Or Folder
                ManagedFolderID     = 0987612345  # Box folder ID created during the bootstrap process. This is the location that the user's data will be moved to.
                PostMigrationAPIUri = 'https://box-migration-test.azurewebsites.net/api/'
                SharedSecret        = $(Get-Credential -Username 'Box Post-Migration API Shared Secret')
            }
            $MoveItemResponse = Invoke-BoxMoveItem @splat

            $CorrelationID     = $MoveItemResponse.Headers.CorrelationID
            $RequestBody       = $MoveItemResponse.RequestBody | ConvertFrom-Json
            $StatusCode        = $MoveItemResponse.Response.StatusCode
            $StatusDescription = $MoveItemResponse.Response.StatusDescription

#>
function Invoke-BoxMoveItem {

    [CmdletBinding()]

    param(

        # The Box user ID number of the user to be migrated.
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string]$UserID,

        # The Box ID of the file/folder being moved.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$ItemID,

        # The item type being moved. Possible values include "File" or "Folder".
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [ValidateSet('File', 'Folder')]
        [string]$ItemType,

        # The Box folder ID created during the bootstrap process. This is the location that the user's data will be moved to.
        [Parameter(Mandatory)]
        [string]$ManagedFolderID,

        # The URI to the Box Post-Migration API service.
        [string]$PostMigrationAPIUri = $script:PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret = $script:SharedSecret,

        # Correlation ID used to track events with the API logs. If one is not provide a new GUID will be used.
        [string]$CorrelationID = (New-Guid),

        # The number of times a retry will be attempted in the event the API call fails.
        [int]$Retry = 3

    )

    begin {

        # If these parameters were not given and the defaults have not be initialized, prompt the user and initialize them now.

        if ($null -eq $PostMigrationAPIUri -or $PostMigrationAPIUri.Length -eq 0 -or $null -eq $SharedSecret) {
            Initialize-BoxConfiguration -ErrorAction Stop
            $PostMigrationAPIUri = $script:PostMigrationAPIUri
            $SharedSecret        = $script:SharedSecret
        }

    }

    process {

        Write-Verbose ('-' * 80)
        Write-Verbose ("$($MyInvocation.MyCommand.Name) : ItemId = $ItemId : CorrelationID = $CorrelationID")

        $RetryCount = 0
        $resp = $null

        $body = @{
            UserId          = $UserID
            ItemId          = $ItemID
            ItemType        = $ItemType.ToLower()
            ManagedFolderId = $ManagedFolderID
        }

        $apiSuccess = $false

        do {

            try {

                $splat = @{
                    APIEndpoint         = 'MoveItem'
                    Parameters          = $body
                    CorrelationID       = $CorrelationID
                    PostMigrationAPIUri = $PostMigrationAPIUri
                    SharedSecret        = $SharedSecret
                    ErrorAction         = 'Stop'
                }

                $resp = Invoke-BoxRequest @splat

                $apiSuccess = $true

            } catch {

                $RetryCount++

                if ($RetryCount -gt $Retry) {
                    throw $_  # Rethrow error response object.
                } else {
                    Write-Warning ($MyInvocation.MyCommand.Name + ' Failed : Response = ' + $_.ErrorDetails.Message + ' : Status Code = ' + $_.Exception.Response.StatusCode.value__ + ' : CorrelationID = ' + $CorrelationID)
                    Write-Warning 'Retrying...'
                }

                Start-Sleep 1

            }

        } until($apiSuccess -or $RetryCount -gt $Retry)

        # Return both the request body and response.

        [PSCustomObject]@{
            RequestBody = $body
            Response    = $resp
        }

    }

}

#endregion Invoke-BoxMoveItem

#=====================================================================================================================================================

#region Invoke-BoxUpdateCollaboration

<#

    .SYNOPSIS

        Update collaboration role on a single item to ‘viewer’ for all collaborators. This will (likely) be called many times per migrated user.

    .OUTPUTS

        Returns an object that contains the custom HTTP headers, HTTP request body, and HTTP response from the UpdateCollaborations endpoint of the
        Box Post-Migration API.

            Headers     - Contains the CorrelationID used during the request.
            RequestBody - Contains the parameters sent in the request body to the Box Post-Migration API endpoint.
            Response    - Returns the actual WebResponseObject from the API endpoint. The contents should be empty and its status code should be 200.

    .EXAMPLE

        Move item to user's managed folder created in the bootstrap process.

            $splat = @{
                UserID              = 1234567890  # Box User ID of migrated user.
                ItemID              = 1234509876  # The file/folder ID of the item being moved.
                ItemType            = File  # Or Folder
                ManagedUserID       = 0987654321  # Box User ID of UITS managed service account for long term storage. Do not confuse this with ManagedFolderID.
                PostMigrationAPIUri = 'https://box-migration-test.azurewebsites.net/api/'
                SharedSecret        = $(Get-Credential -Username 'Box Post-Migration API Shared Secret')
            }
            $UpdateCollabResponse = Invoke-BoxUpdateCollaboration @splat

            $CorrelationID     = $UpdateCollabResponse.Headers.CorrelationID
            $RequestBody       = $UpdateCollabResponse.RequestBody | ConvertFrom-Json
            $StatusCode        = $UpdateCollabResponse.Response.StatusCode
            $StatusDescription = $UpdateCollabResponse.Response.StatusDescription

#>
function Invoke-BoxUpdateCollaboration {

    [CmdletBinding()]

    param(

        # The Box user ID number of the user to be migrated.
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string]$UserID,

        # The Box ID of the file/folder being set to viewer only for all collaborators.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$ItemID,

        # The item type being updated. Possible values include "File" or "Folder".
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [ValidateSet('File', 'Folder')]
        [string]$ItemType,

        # The Box user ID of the managed user who will hold the migrated data in Box.
        #
        # When a user's data is migrated, the copy in Box will have its ownership changed to this managed user.
        [Parameter(Mandatory)]
        [string]$ManagedUserID,

        # The URI to the Box Post-Migration API service.
        [string]$PostMigrationAPIUri = $script:PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret = $script:SharedSecret,

        # Correlation ID used to track events with the API logs. If one is not provide a new GUID will be used.
        [string]$CorrelationID = (New-Guid),

        # The number of times a retry will be attempted in the event the API call fails.
        [int]$Retry = 3

    )

    begin {

        # If these parameters were not given and the defaults have not be initialized, prompt the user and initialize them now.

        if ($null -eq $PostMigrationAPIUri -or $PostMigrationAPIUri.Length -eq 0 -or $null -eq $SharedSecret) {
            Initialize-BoxConfiguration -ErrorAction Stop
            $PostMigrationAPIUri = $script:PostMigrationAPIUri
            $SharedSecret        = $script:SharedSecret
        }

    }

    process {

        Write-Verbose ('-' * 80)
        Write-Verbose ("$($MyInvocation.MyCommand.Name) : ItemId = $ItemId : CorrelationID = $CorrelationID")

        $RetryCount = 0
        $resp = $null

        $body = @{
            UserId        = $UserID
            ItemId        = $ItemID
            ItemType      = $ItemType.ToLower()
            ManagedUserId = $ManagedUserID
        }

        $apiSuccess = $false

        do {

            try {

                $splat = @{
                    APIEndpoint         = 'UpdateCollaborations'
                    Parameters          = $body
                    CorrelationID       = $CorrelationID
                    PostMigrationAPIUri = $PostMigrationAPIUri
                    SharedSecret        = $SharedSecret
                    ErrorAction         = 'Stop'
                }

                $resp = Invoke-BoxRequest @splat

                $apiSuccess = $true

            } catch {

                $RetryCount++

                if ($RetryCount -gt $Retry) {
                    throw $_  # Rethrow error response object.
                } else {
                    Write-Warning ($MyInvocation.MyCommand.Name + ' Failed : Response = ' + $_.ErrorDetails.Message + ' : Status Code = ' + $_.Exception.Response.StatusCode.value__ + ' : CorrelationID = ' + $CorrelationID)
                    Write-Warning 'Retrying...'
                }

                Start-Sleep 1

            }

        } until($apiSuccess -or $RetryCount -gt $Retry)

        # Return both the request body and response.

        [PSCustomObject]@{
            RequestBody = $body
            Response    = $resp
        }

    }

}

#endregion Invoke-BoxUpdateCollaboration

#=====================================================================================================================================================

#region Invoke-BoxCleanUp

<#

    .SYNOPSIS

        Finalize the managed folder by making the migrated user a viewer collaborator on all content. This will be called once per migrated user.

    .OUTPUTS

        Returns an object that contains the custom HTTP headers, HTTP request body, and HTTP response from the CleanUp endpoint of the Box
        Post-Migration API.

            Headers     - Contains the CorrelationID used during the request.
            RequestBody - Contains the parameters sent in the request body to the Box Post-Migration API endpoint.
            Response    - Returns the actual WebResponseObject from the API endpoint. The contents should be empty and its status code should be 200.

    .EXAMPLE

        Finalize a migrated user by setting them to view only on the managed folder created during Bootstrap process.

            $splat = @{
                UserID              = 1234567890  # Box User ID of migrated user.
                ManagedUserID       = 0987654321  # Box User ID of UITS managed service account for long term storage. Do not confuse this with ManagedFolderID.
                ManagedFolderID     = 0987612345  # Box folder ID created during the bootstrap process. This is the location that the user's data will be moved to.
                PostMigrationAPIUri = 'https://box-migration-test.azurewebsites.net/api/'
                SharedSecret        = $(Get-Credential -Username 'Box Post-Migration API Shared Secret')
            }
            $CleanUpResponse = Invoke-BoxCleanUp @splat

            $CorrelationID     = $CleanUpResponse.Headers.CorrelationID
            $RequestBody       = $CleanUpResponse.RequestBody | ConvertFrom-Json
            $StatusCode        = $CleanUpResponse.Response.StatusCode
            $StatusDescription = $CleanUpResponse.Response.StatusDescription

#>
function Invoke-BoxCleanUp {

    [CmdletBinding()]

    param(

        # The Box user ID number of the user to be migrated.
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [string]$UserID,

        # The Box user ID of the managed user who will hold the migrated data in Box.
        #
        # When a user's data is migrated, the copy in Box will have its ownership changed to this managed user.
        [Parameter(Mandatory)]
        [string]$ManagedUserID,

        # The Box folder ID created during the bootstrap process. This is the location that the user's data will be moved to.
        [Parameter(Mandatory)]
        [string]$ManagedFolderID,

        # The URI to the Box Post-Migration API service.
        [string]$PostMigrationAPIUri = $script:PostMigrationAPIUri,

        # The shared secret needed to access the Box Post-Migration API service. No username is needed, just enter the shared secret as the password.
        [System.Management.Automation.CredentialAttribute()]
        [PSCredential]$SharedSecret = $script:SharedSecret,

        # Correlation ID used to track events with the API logs. If one is not provide a new GUID will be used.
        [string]$CorrelationID = (New-Guid)

    )

    begin {

        # If these parameters were not given and the defaults have not be initialized, prompt the user and initialize them now.

        if ($null -eq $PostMigrationAPIUri -or $PostMigrationAPIUri.Length -eq 0 -or $null -eq $SharedSecret) {
            Initialize-BoxConfiguration -ErrorAction Stop
            $PostMigrationAPIUri = $script:PostMigrationAPIUri
            $SharedSecret        = $script:SharedSecret
        }

    }

    process {

        Write-Verbose ('=' * 80)
        Write-Verbose ("$($MyInvocation.MyCommand.Name) : UserID = $UserID : CorrelationID = $CorrelationID")

        $body = @{
            UserId = $UserID
            ManagedUserId = $ManagedUserID
            ManagedFolderId = $ManagedFolderID
        }

        $splat = @{
            APIEndpoint         = 'Cleanup'
            Parameters          = $body
            CorrelationID       = $CorrelationID
            PostMigrationAPIUri = $PostMigrationAPIUri
            SharedSecret        = $SharedSecret
            ErrorAction         = 'Stop'
        }

        $resp = Invoke-BoxRequest @splat

        # Return both the request body and response.

        [PSCustomObject]@{
            RequestBody = $body
            Response    = $resp
        }

    }

}

#endregion Invoke-BoxCleanUp

#=====================================================================================================================================================

#region Invoke-ProcessTransferJob

<#

    .SYNOPSIS

        This helper function is used only to insert new job records into the TransferJobs table.

    .DESCRIPTION

        This helper function is used only to insert new job records into the TransferJobs table.

        It will use a steppable pipeline and the Invoke-CCIStoreProcedure command from CCITaskTools module. This allows us to avoid opening/closing
        the DB connection for every item. Normally this could be done just by piping directly to Invoke-CCIStoreProcedure, but we need to perform some
        additional processing after the successful call for each item.

#>
function Invoke-ProcessTransferJob {

    [CmdletBinding()]

    param(

        # The transfer job to be received from the pipeline.
        [Parameter(Mandatory, ValueFromPipeline)]
        [PSCustomObject]$TransferJob,

        # The SQL server to run the Stored Procedure on.
        [Parameter(Mandatory)]
        [string]$Server,

        # The SQL database the Stored Procedure belongs to.
        [Parameter(Mandatory)]
        [string]$Database,

        # The Stored Procedure name to execute.
        [Parameter(Mandatory)]
        [string]$StoredProcedure,

        # The file path of the Last Job file.
        [Parameter(Mandatory)]
        [string]$LastJobFilePath,

        # The amount of time in seconds to wait on the database request before failing.
        [int]$TimeoutSec = 90,

        # This switch will force the provided TransferJob to pass through to the next command on the pipeline.
        [switch]$PassThru

    )

    begin {

        # If we are using the default value we need to pass it on to Invoke-CCIStoredProcedure along with the other bound parameters.
        $PSBoundParameters['TimeoutSec'] = $TimeoutSec

        # Remove the extra parameter that Invoke-CCIStoredProcedure doesn't have.
        $PSBoundParameters.Remove('LastJobFilePath') | Out-Null
        $PSBoundParameters.Remove('PassThru') | Out-Null

        $InvokeSPCmd = $ExecutionContext.InvokeCommand.GetCommand('Invoke-CCIStoredProcedure', [System.Management.Automation.CommandTypes]::Function)
        $ScriptCmd = { & $InvokeSPCmd @PSBoundParameters }
        $SteppablePipeline = $ScriptCmd.GetSteppablePipeline($MyInvocation.CommandOrigin)

        # Call the Begin section of pipeline which includes Invoke-CCIStoredProcedure's Begin section and it will open the SQL connection.

        $SteppablePipeline.Begin($PSCmdlet)

    }

    process {

        $currJob = [PSCustomObject]@{
            JobID = $TransferJob.ID
            AccountEmail = $TransferJob.AccountEmail
            DisplayName = $TransferJob.DisplayName
        }

        # Call the Process section of pipeline and Invoke-CCIStoredProcedure will process the current job.

        try {
            Write-Verbose "Adding new job : JobID = $($TransferJob.ID) : DisplayName = $($TransferJob.DisplayName) : AccountEmail = $($TransferJob.AccountEmail)"
            $SteppablePipeline.Process($currJob)
        } catch {
            # Make sure to close the SQL connection if we terminate and throw an error.
            $SteppablePipeline.End()
            $subject = "SkySync : Error inserting new transfer job into database : JobID = $($TransferJob.ID) : DisplayName = $($TransferJob.DisplayName)"
            Write-Error -Exception $_.Exception -Message $subject -ErrorAction Stop
        }

        # Update last job file. The first 3 are simply used by us humans.

        $LastJobDetails = [PSCustomObject]@{
            JobID              = $currJob.JobID
            DisplayName        = $currJob.DisplayName
            CompletedDateTime  = $TransferJob.CompletedDateTime
            CompletedTimestamp = $TransferJob.CompletedTimestamp
        }

        # Write out updated details about the last job synced via this script.

        $LastJobDetails | Export-Csv -Path $LastJobFilePath -NoTypeInformation -Force -ErrorAction Stop

        if ($PassThru) {
            $TransferJob
        }

    }

    end {

        # Close up the SQL connection by calling the End section of the pipeline.

        $SteppablePipeline.End()

    }

}

#endregion Invoke-ProcessTransferJob

#=====================================================================================================================================================

#region Set-TransferJobFlags

<#

    .SYNOPSIS

        This helper function is used to set the flags for a given Box transfer job.

    .DESCRIPTION

        This helper function is used to set the flags for a given Box transfer job.

        It will use a steppable pipeline and the Invoke-CCIStoreProcedure command from CCITaskTools module. This allows us to avoid opening/closing
        the DB connection for every item. Normally this could be done just by piping directly to Invoke-CCIStoreProcedure, but we need to perform some
        additional processing before making a call on each item.

#>
function Set-TransferJobFlags {

    [CmdletBinding()]

    param(

        # The transfer job to be received from the pipeline.
        [Parameter(Mandatory, ValueFromPipeline)]
        [PSCustomObject]$TransferJob,

        # The SQL server to run the Stored Procedure on.
        [Parameter(Mandatory)]
        [string]$Server,

        # The SQL database the Stored Procedure belongs to.
        [Parameter(Mandatory)]
        [string]$Database,

        # The Stored Procedure name to execute.
        [Parameter(Mandatory)]
        [string]$StoredProcedure,

        # The amount of time in seconds to wait on the database request before failing.
        [int]$TimeoutSec = 90,

        # This switch will force the provided TransferJob to pass through to the next command on the pipeline.
        [switch]$PassThru

    )

    begin {

        # If we are using the default value we need to pass it on to Invoke-CCIStoredProcedure along with the other bound parameters.
        $PSBoundParameters['TimeoutSec'] = $TimeoutSec

        # Remove the extra parameter that Invoke-CCIStoredProcedure doesn't have.
        $PSBoundParameters.Remove('PassThru') | Out-Null

        $InvokeSPCmd = $ExecutionContext.InvokeCommand.GetCommand('Invoke-CCIStoredProcedure', [System.Management.Automation.CommandTypes]::Function)
        $ScriptCmd = { & $InvokeSPCmd @PSBoundParameters }
        $SteppablePipeline = $ScriptCmd.GetSteppablePipeline($MyInvocation.CommandOrigin)

        # Call the Begin section of pipeline which includes Invoke-CCIStoredProcedure's Begin section and it will open the SQL connection.

        $SteppablePipeline.Begin($PSCmdlet)

    }

    process {

        $SkipAllTasks     = 0
        $SkipCleanUpTasks = 0

        switch -Wildcard ($TransferJob.DisplayName) {

            # Teams group migrations.
            '*-> SPO*' { $SkipCleanUpTasks = 1 }

            # Google Shared Drive group migrations.
            '*-> GSD*' { $SkipCleanUpTasks = 1 }

            # FromBox2 are retries and need to skip all post-processing.
            'FromBox2 ->*' { $SkipAllTasks = 1 }

        }

        # The database defaults to skip all tasks just in case this task here fails to update properly.
        # So we only need to update if a flag is set to NOT skip a task.

        if((-not $SkipAllTasks) -or (-not $SkipCleanUpTasks)) {

            $currJob = [PSCustomObject]@{
                JobID            = $TransferJob.ID
                SkipAllTasks     = $SkipAllTasks
                SkipCleanUpTasks = $SkipCleanUpTasks
            }

            # Call the Process section of pipeline and Invoke-CCIStoredProcedure will process the current job.

            try {
                Write-Verbose "Setting job flags : JobID = $($TransferJob.ID) : SkipAllTasks = $SkipAllTasks : SkipCleanUpTasks = $SkipCleanUpTasks"
                $SteppablePipeline.Process($currJob)
            } catch {
                # Make sure to close the SQL connection if we terminate and throw an error.
                $SteppablePipeline.End()
                $subject = "SkySync : Error setting transfer job flags in database : JobID = $($TransferJob.ID) : SkipAllTasks = $SkipAllTasks : SkipCleanUpTasks = $SkipCleanUpTasks"
                Write-Error -Exception $_.Exception -Message $subject -ErrorAction Stop
            }

        }

        if ($PassThru) {
            $TransferJob
        }

    }

    end {

        # Close up the SQL connection by calling the End section of the pipeline.

        $SteppablePipeline.End()

    }

}

#endregion Set-TransferJobFlags

#=====================================================================================================================================================

#region Send-BoxCompletionEmail

<#

    .SYNOPSIS

        This helper function is used to send the Box migration completion email to the user.

    .DESCRIPTION

        This helper function is used to send the Box migration completion email to the user.

#>
function Send-BoxCompletionEmail {

    [CmdletBinding()]

    param(

        # The transfer job to be received from the pipeline.
        [Parameter(Mandatory, ValueFromPipeline)]
        [PSCustomObject]$TransferJob,

        # The file path to the migration complete email.
        [Parameter(Mandatory)]
        [string]$MigrationCompleteEmailFilePath,

        # The file path to the migration complete email for group accounts.
        [Parameter(Mandatory)]
        [string]$MigrationCompleteEmailFilePathGroups,

        # This switch will force the provided TransferJob to pass through to the next command on the pipeline.
        [switch]$PassThru

    )

    begin {

        $from = 'ithelp@iu.edu'
        $subject = 'Your Box files have been migrated - see your migration report'
        $mailRelay = 'mail-relay.iu.edu'
        $mailRelayPort = 587

    }

    process {

        $SkipAllTasks, $SkipCleanUpTasks = 0

        $to = (Get-ADUser -LDAPFilter "(ExtensionAttribute8=$($TransferJob.AccountEmail))" -Properties mail).mail

        switch -Wildcard ($TransferJob.DisplayName) {

            # OneDrive user migrations.
            '*-> ODB -*' {
                $EmailFilePath = $MigrationCompleteEmailFilePath
                $service       = 'Microsoft OneDrive at IU'
                $findYourFiles = '<b>Find your files.</b> Go to <a href="https://onedrive.iu.edu/">onedrive.iu.edu</a> and click the "Log in to Office 365" button. Log in with your IU credentials.'
            }

            # Teams group migrations.
            '*-> SPO*' {
                $EmailFilePath    = $MigrationCompleteEmailFilePathGroups
                $service          = 'Microsoft SharePoint Online at IU'
                $findYourFiles    = 'TBD'
            }

            # Google Shared Drive group migrations.
            '*-> GSD*' {
                $EmailFilePath    = $MigrationCompleteEmailFilePathGroups
                $service          = 'Google Shared Drives at IU'
                $findYourFiles    = 'TBD'
            }

            # Default to Google MyDrive user migrations.
            Default {
                $EmailFilePath = $MigrationCompleteEmailFilePath
                $service       = 'Google at IU My Drive'
                $findYourFiles = '<b>Find your files.</b> Go to <a href="https://google.iu.edu/">google.iu.edu</a>. Under Google at IU Apps, click the "Log in to Google at IU" button. Log in with your IU credentials.'
            }

        }

        # Send the email.

        try {

            Write-Verbose "Sending Box Migration Completion email: To = $to | Service = `"$service`" | Job Name = $($TransferJob.DisplayName) ..."

            $body = (Get-Content -Path $EmailFilePath -Raw -ErrorAction Stop) -replace '{{SERVICE}}', $service

            $body = $body -replace '{{FINDYOURFILES}}', $findYourFiles

            Send-MailMessage -To $to -From $from -Subject $subject -Body $body -BodyAsHtml -SmtpServer $mailRelay -Port $mailRelayPort -UseSsl -ErrorAction Stop

        } catch {

            $subject = "Error sending migration complete email to user : $to"
            Write-Error -Exception $_.Exception -Message $subject -ErrorAction Stop

        }

        if ($PassThru) {
            $TransferJob
        }

    }

}

#endregion Send-BoxCompletionEmail