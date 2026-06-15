resource "random_uuid" "workbook" {}

resource "azurerm_application_insights_workbook" "main" {
  name                = random_uuid.workbook.result
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  display_name        = "Protocol Indexer — Observability"

  data_json = jsonencode({
    version = "Notebook/1.0"
    items = [
      # ── Section: Azure AI Search ──────────────────────────────────────────
      {
        type = 1
        content = { json = "## Azure AI Search" }
        name   = "search-header"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            AzureDiagnostics
            | where ResourceProvider == "MICROSOFT.SEARCH"
            | where OperationName == "Query.Search"
            | summarize AvgLatencyMs = avg(DurationMs), P95LatencyMs = percentile(DurationMs, 95) by bin(TimeGenerated, 5m)
            | render timechart
          QUERY
          size          = 0
          title         = "Query Latency (avg + p95)"
          timeContext   = { durationMs = 3600000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "timechart"
        }
        name = "query-latency"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            AzureDiagnostics
            | where ResourceProvider == "MICROSOFT.SEARCH"
            | where tostring(resultSignature_d) == "503"
            | summarize ThrottledRequests = count() by bin(TimeGenerated, 5m)
            | render timechart
          QUERY
          size          = 0
          title         = "Throttling (503s)"
          timeContext   = { durationMs = 3600000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "timechart"
        }
        name = "throttling"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            AzureDiagnostics
            | where ResourceProvider == "MICROSOFT.SEARCH"
            | where OperationName startswith "Indexing"
            | summarize IndexingOps = count() by bin(TimeGenerated, 5m)
            | render timechart
          QUERY
          size          = 0
          title         = "Indexing Activity"
          timeContext   = { durationMs = 3600000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "timechart"
        }
        name = "indexing-activity"
      },
      # ── Section: ACI Lifecycle ────────────────────────────────────────────
      {
        type = 1
        content = { json = "## ACI Lifecycle" }
        name   = "aci-header"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            ContainerEvent_CL
            | where ContainerGroup_s == "aci-invoice-indexer-dev"
            | project TimeGenerated, Message, Reason_s, Type_s
            | order by TimeGenerated desc
          QUERY
          size          = 0
          title         = "Container Events"
          timeContext   = { durationMs = 86400000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "table"
        }
        name = "container-events"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            ContainerEvent_CL
            | where ContainerGroup_s == "aci-invoice-indexer-dev"
            | where Message contains "ExitCode"
            | where Message !contains "ExitCode 0"
            | project TimeGenerated, Message
            | order by TimeGenerated desc
          QUERY
          size          = 0
          title         = "Crashes (non-zero exit codes)"
          timeContext   = { durationMs = 86400000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "table"
        }
        name = "crashes"
      },
      # ── Section: Pipeline Logs ────────────────────────────────────────────
      {
        type = 1
        content = { json = "## Pipeline Logs" }
        name   = "pipeline-header"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            ContainerInstanceLog_CL
            | where ContainerGroup_s == "aci-invoice-indexer-dev"
            | where Message contains "Pipeline started" or Message contains "Pipeline complete"
            | project TimeGenerated, Message
            | order by TimeGenerated desc
          QUERY
          size          = 0
          title         = "Pipeline Start / Complete"
          timeContext   = { durationMs = 86400000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "table"
        }
        name = "pipeline-runs"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            ContainerInstanceLog_CL
            | where ContainerGroup_s == "aci-invoice-indexer-dev"
            | where Message contains "Failed to process"
            | project TimeGenerated, Message
            | order by TimeGenerated desc
          QUERY
          size          = 0
          title         = "Per-Document Failures"
          timeContext   = { durationMs = 86400000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "table"
        }
        name = "doc-failures"
      },
      {
        type = 3
        content = {
          version       = "KqlItem/1.0"
          query         = <<-QUERY
            ContainerInstanceLog_CL
            | where ContainerGroup_s == "aci-invoice-indexer-dev"
            | where Message contains "Exception" or Message contains "Error" or Message contains "Failed"
            | project TimeGenerated, Message
            | order by TimeGenerated desc
          QUERY
          size          = 0
          title         = "Errors and Exceptions"
          timeContext   = { durationMs = 86400000 }
          queryType     = 0
          resourceType  = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          visualization = "table"
        }
        name = "errors"
      }
    ]
  })

  tags = {
    project     = "support-agent"
    environment = "dev"
  }
}