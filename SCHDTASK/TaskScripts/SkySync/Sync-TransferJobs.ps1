#Requires -Modules CCILogger, CCITaskTools, CCISecrets, SQLServer, ActiveDirectory

<#

This script runs as a scheduled task to retrieve completed migration details from SkySync database server and syncs them to the CCI.SecureStorage.Web
application database. This is done for two reasons:

    1. A second scheduled task script will execute once a migration is flagged as completed, which will perform a series of post-migration tasks that
    clean-up and finalize the migration.

    2. This data can be used by the web application later for reporting purposes (i.e. ensuring both migration and post-migrations tasks completed).

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
    $Configuration = Get-Content -Path $ConfigurationFilePath | ConvertFrom-Json
} catch {

    $subject = "SkySync [$($env:COMPUTERNAME + ':' + $env:USERDOMAIN)] : Error retrieving configuration file for $($PSCmdlet.MyInvocation.MyCommand)"
    $_ | Write-CCIEmailLog -ToEmailAddress 'ads-admin@iu.edu' -EmailSubject $subject -Verbose:$IsVerbose
    throw $subject

}

# Retrieve SkySync database credentials (we don't need the Box API key in this script so it gets sent to $null).

try {
    if (Test-Path $Configuration.CredentialsPath -ErrorAction Ignore) {
        $SkySyncCreds, $null = Get-CCICredentialFromFile -Path $Configuration.CredentialsPath -ErrorAction Stop -Verbose:$IsVerbose
    } else { throw 'Path to credentials could not be found. : ' + $Configuration.CredentialsPath }
} catch {
    throw ('Could not retrieve credentials. : ' + $_.Exception.Message)
}

# Setup script state object. This is mostly just to provide diagnostics for error emails.

$ScriptState = [ordered]@{
    ScriptName       = $PSCmdlet.MyInvocation.MyCommand
    TaskServer       = $env:COMPUTERNAME
    TaskServerDomain = $env:USERDOMAIN
}

# Get details about the last job synced via this script.

try {

    if (Test-Path $Configuration.LastJobFilePath -ErrorAction Ignore) {
        $LastJobDetails = Import-Csv -Path $Configuration.LastJobFilePath -ErrorAction Stop
    } else { throw 'Last run time file could not be found. If this is a first time run see Setup-Notes.ps1 for details about creating an initial file. : ' + $Configuration.LastJobFilePath }

} catch {

    $subject = 'SkySync : Could not load last run time file'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}

# Retrieve all transfer jobs after the last processed JobID.

try {

    $qTransferJobs = "
SELECT
    TransferJobs.ID,
    ScheduledJobs.Name AS DisplayName,
    TransferJobs.SourceAccountEmail AS AccountEmail,
    DATEADD(HOUR, -6, DATEADD(S, JobExecutions.EndTime, '1970-01-01')) AS CompletedDate,
    JobExecutions.EndTime AS CompletedTimestamp
FROM [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].TransferJobs
    LEFT JOIN [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].ScheduledJobs
        ON TransferJobs.ID = ScheduledJobs.ID
    LEFT JOIN [$($Configuration.SkySyncDatabase)].[$($Configuration.SkySyncDatabaseSchema)].JobExecutions
        ON JobExecutions.ID = ScheduledJobs.LastExecutionID
WHERE IsActive = 1
    AND IsCompleted = 1
    AND UseSimulationMode = 0
    AND EndTime > $($LastJobDetails.CompletedTimestamp)
ORDER BY EndTime
"

    $TransferJobs = @()
    $TransferJobs = Invoke-Sqlcmd -Credential $SkySyncCreds -ServerInstance $Configuration.SkySyncSQLServer -Query $qTransferJobs -ErrorAction Stop -Verbose:$IsVerbose

} catch {

    $subject = 'SkySync : Error gathering transfer jobs'
    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}

# Dump job details into a table for later use, along with a few extra attributes and flags.
# Also send a completion email for each job added to the user.

try {

    $processJobSplat = @{
        Server          = $Configuration.WebAppSQLServer
        Database        = $Configuration.WebAppDatabase
        StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.InsertTransferJob')
        LastJobFilePath = $Configuration.LastJobFilePath
        ErrorAction     = 'Stop'
        Verbose         = $IsVerbose
        PassThru        = $true
    }

    $setFlagsSplat = @{
        Server          = $Configuration.WebAppSQLServer
        Database        = $Configuration.WebAppDatabase
        StoredProcedure = ($Configuration.WebAppDatabaseSchema + '.SetJobSkipFlags')
        ErrorAction     = 'Stop'
        Verbose         = $IsVerbose
        PassThru        = $true
    }

    $sendEmailSplat = @{
        MigrationCompleteEmailFilePath       = $Configuration.MigrationCompleteEmailFilePath
        MigrationCompleteEmailFilePathGroups = $Configuration.MigrationCompleteEmailFilePathGroups
        Verbose                              = $IsVerbose
    }

    if ($TransferJobs.Count -eq 0) {
        Write-Verbose 'No new jobs'
    } else {
        Write-Verbose "Inserting new jobs to server:database: $($Configuration.WebAppSQLServer):$($Configuration.WebAppDatabase)"
    }
    $TransferJobs | Invoke-ProcessTransferJob @processJobSplat | Set-TransferJobFlags @setFlagsSplat | Send-BoxCompletionEmail @sendEmailSplat

} catch {

    $subject = 'SkySync : Error syncing transfer job to database'

    $Configuration, [PSCustomObject]$ScriptState, $_ | Write-CCIEmailLog -ToEmailAddress $Configuration.ErrorToAddress -EmailSubject $subject -Digest -Verbose:$IsVerbose
    throw $_

}
