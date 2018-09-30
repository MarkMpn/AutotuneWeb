# AutotuneWeb

This project aims to simplify the process of running [Autotune](https://openaps.readthedocs.io/en/latest/docs/Customize-Iterate/autotune.html)
for non-OpenAPS users, e.g. people running [AndroidAPS](http://androidaps.readthedocs.io/en/latest/EN/index.html).

There are two main steps in the project:

1. Converting a profile (basal rates, IC and CR values) from [Nightscout](http://www.nightscout.info/) into a format ready to be used with Autotune, and
2. Optionally, automatically running Autotune on the converted profile and emailing the results back once complete

To implement this there are several resources required. This has been designed to run in Azure but could be adapted to run elsewhere:

1. Website to receive user input and convert Nightscout profile to OpenAPS format
2. Database to store the details of the profile to run
3. Scheduled web jobs to automatically start/stop a virtual machine to run Autotune
4. Linux virtual machine with the following systems installed:
  1. Autotune
  2. AutotuneRunner to pick up next profile to run from the database and launch Autotune
  3. cron job to start AutotuneRunner on a regular basis
5. [SendGrid](https://sendgrid.com/) account to send the Autotune results by email

## Database Setup

Create an Azure SQL Database and create a table `Jobs` as follows:

```sql
CREATE TABLE [dbo].[Jobs](
	[JobID] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
	[NSUrl] [varchar](255) NOT NULL,
	[Profile] [varchar](max) NOT NULL,
	[CreatedAt] [datetime] NOT NULL,
	[Units] [varchar](10) NOT NULL,
	[CategorizeUAMAsBasal] [bit] NOT NULL,
	[PumpBasalIncrement] [numeric](4, 3) NOT NULL,
	[EmailResultsTo] [varchar](max) NOT NULL,
	[ProcessingStarted] [datetime] NULL,
	[ProcessingCompleted] [datetime] NULL,
	[Result] [varchar](max) NULL,
	[Failed] [bit] NULL
)
```

## Website Setup

Create an Azure App Service and deploy the AutotuneWeb project to it.

In the App Service configuration, go to the `Application settings` option and add a connection string with the following details:

* Name: `Sql`
* Value: /Connection string for the database you created earlier/
* Type: `SQLAzure`

## Virtual Machine Setup

Create a Linux Virtual Machine and install Autotune as described in the Autotune documentation

[Install the .NET Core runtime](https://docs.microsoft.com/en-us/dotnet/core/linux-prerequisites)

Build & deploy the AutotuneRunner project to a new directory, e.g. ~/autotunerunner

Set up a cron job to run AutotuneRunner regularly to check for any new jobs to run:

```
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin:/home/user/.dotnet/tools
AUTOTUNE_CONNECTIONSTRING=database-connection-string
AUTOTUNE_ROOT=/home/user
SENDGRID_API_KEY=sendgrid-api-key
SENDGRID_FROM_ADDRESS=email-address

*/1 * * * * cd ~/myopenaps && dotnet ~/autotunerunner/AutotuneRunner.dll
```

In this crontab file, make the following adjustments:

* **/home/user** - change this to your user directory
* **database-connection-string** - change this to the connection string for the SQL database you created earlier
* **sendgrid-api-key** - change this to the API key for your SendGrid account
* **email-address** - change this to the email address you want the automatically generated emails to be sent from

## Starting/Stopping the Virtual Machine Automatically

As the virtual machine costs for the time it is running, we want it to only run when there is a job in the database
that is waiting to be processed, and it to shut down as soon as the final job has finished. We can do this with a
WebJob running within the context of the website App Service.

```ps
$username = 'service-principal-id'
$password = ConvertTo-SecureString 'password' -AsPlainText -Force
$connStr = 'connection-string'
$resourcegroup = 'resource-group'
$vmname = 'vm-name'
$tenantid = 'tenant-id'

$credentials = New-Object System.Management.Automation.PSCredential $username, $password
$AzureRMAccount = Add-AzureRmAccount -Credential $credentials -ServicePrincipal -TenantId $tenantid

$vmstatus = Get-AzureRMVM -ResourceGroupName $resourcegroup -name $vmname -Status

$con = New-Object System.Data.SqlClient.SqlConnection $connStr
$con.Open()

$cmd = $con.CreateCommand()
$cmd.CommandText = 'SELECT count(*) FROM Jobs WHERE ProcessingCompleted IS NULL'

$queueLength = $cmd.ExecuteScalar()

$cmd.Dispose()
$con.Dispose()

Write-Output "VM status $($vmstatus.Statuses[1].code)"
Write-Output "Queue length $queueLength"

if ($vmstatus.Statuses[1].code -eq 'PowerState/deallocated' -and $queueLength -gt 0)
{
	Write-Output 'VM stopped and jobs in queue - starting'
	Start-AzureRMVM -ResourceGroupName $resourcegroup -Name $vmname
}

if ($vmstatus.Statuses[1].code -eq 'PowerState/running' -and $queueLength -eq 0)
{
	Write-Output 'VM running and no jobs in queue - stopping'
	Stop-AzureRMVM -ResourceGroupName $resourcegroup -Name $vmname -Force
}
```