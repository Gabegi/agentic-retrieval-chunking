resource "azurerm_monitor_action_group" "main" {
  name                = "ag-support-agent-dev"
  resource_group_name = azurerm_resource_group.main.name
  short_name          = "searchalert"

  email_receiver {
    name          = "admin"
    email_address = "gabriel.pirastru@devoteam.com"
  }
}

# 1. Throttling — fires when any 503 occurs
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "throttling" {
  name                = "alert-search-throttling-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 2

  criteria {
    query = <<-QUERY
      AzureDiagnostics
      | where ResourceProvider == "MICROSOFT.SEARCH"
      | where tostring(resultSignature_d) == "503"
      | summarize ThrottledRequests = count()
      | where ThrottledRequests > 0
    QUERY

    time_aggregation_method = "Count"
    threshold               = 1
    operator                = "GreaterThanOrEqual"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.main.id]
  }

  description = "Fires when any 503 throttling occurs on the search service"
  enabled     = true
}

# 2. Query latency — fires when average latency exceeds 2000ms
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "query_latency" {
  name                = "alert-search-query-latency-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 2

  criteria {
    query = <<-QUERY
      AzureDiagnostics
      | where ResourceProvider == "MICROSOFT.SEARCH"
      | where OperationName == "Query.Search"
      | summarize AvgLatencyMs = avg(DurationMs)
    QUERY

    time_aggregation_method = "Average"
    threshold               = 2000
    operator                = "GreaterThan"
    metric_measure_column   = "AvgLatencyMs"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.main.id]
  }

  description = "Fires when average query latency exceeds 2000ms"
  enabled     = true
}

# 3. Indexing impact — fires when indexing and high latency overlap
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "indexing_impact" {
  name                = "alert-search-indexing-impact-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 3

  criteria {
    query = <<-QUERY
      let indexing = AzureDiagnostics
        | where ResourceProvider == "MICROSOFT.SEARCH"
        | where OperationName startswith "Indexing"
        | summarize IndexingCount = count() by bin(TimeGenerated, 1m);
      let searchOps = AzureDiagnostics
        | where ResourceProvider == "MICROSOFT.SEARCH"
        | where OperationName == "Query.Search"
        | summarize AvgLatencyMs = avg(DurationMs) by bin(TimeGenerated, 1m);
      indexing
      | join kind=inner searchOps on TimeGenerated
      | where AvgLatencyMs > 2000 and IndexingCount > 0
      | summarize Count = count()
      | where Count > 0
    QUERY

    time_aggregation_method = "Count"
    threshold               = 1
    operator                = "GreaterThanOrEqual"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.main.id]
  }

  description = "Fires when indexing activity coincides with high query latency"
  enabled     = true
}

# Azure OpenAI diagnostic logging — flows GPT-4o and embedding calls into Log Analytics
resource "azurerm_monitor_diagnostic_setting" "openai" {
  name                       = "diag-openai"
  target_resource_id         = azurerm_cognitive_account.openai.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# 4. GPT-4o extraction latency > 10 seconds
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "gpt_latency" {
  name                = "alert-openai-gpt-latency-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 2

  criteria {
    query = <<-QUERY
      AzureDiagnostics
      | where ResourceProvider == "MICROSOFT.COGNITIVESERVICES"
      | where OperationName == "ChatCompletions_Create"
      | summarize AvgLatencyMs = avg(DurationMs)
      | where AvgLatencyMs > 10000
    QUERY

    time_aggregation_method = "Average"
    threshold               = 10000
    operator                = "GreaterThan"
    metric_measure_column   = "AvgLatencyMs"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.main.id]
  }

  description = "Fires when GPT-4o extraction calls exceed 10 seconds average"
  enabled     = true
}

# 5. OpenAI throttling — fires when any 429 occurs
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "openai_throttling" {
  name                = "alert-openai-throttling-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 2

  criteria {
    query = <<-QUERY
      AzureDiagnostics
      | where ResourceProvider == "MICROSOFT.COGNITIVESERVICES"
      | where toint(resultSignature_d) == 429
      | summarize ThrottledRequests = count()
      | where ThrottledRequests > 0
    QUERY

    time_aggregation_method = "Count"
    threshold               = 1
    operator                = "GreaterThanOrEqual"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.main.id]
  }

  description = "Fires when OpenAI returns 429 throttling errors"
  enabled     = true
}