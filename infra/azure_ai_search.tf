resource "azurerm_search_service" "main" {
  name                = "srch-rag-chunking-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "standard" # Required for semantic ranker + vector search

  semantic_search_sku = "standard" # Enables semantic reranking

  local_authentication_enabled = false # Force Entra ID auth only
  identity {
    type = "SystemAssigned" # Needed to access blob storage
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

# Allow SP to create/manage indexes on the search service
resource "azurerm_role_assignment" "sp_search_index_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_monitor_diagnostic_setting" "search" {
  name                       = "diag-search"
  target_resource_id         = azurerm_search_service.main.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# Allow AI Search to read from blob storage
resource "azurerm_role_assignment" "search_blob_reader" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_search_service.main.identity[0].principal_id
}

# Cognitive Services User (not OpenAI User) is required for knowledge base query planning via the Foundry resource
resource "azurerm_role_assignment" "search_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_search_service.main.identity[0].principal_id
}

# Allow AI Search to read its own index at query time (required for knowledge base retrieval)
resource "azurerm_role_assignment" "search_index_data_reader" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = azurerm_search_service.main.identity[0].principal_id
}

# Allow admin user to query the search service from the portal (local_authentication_enabled = false forces Entra ID auth)
resource "azurerm_role_assignment" "admin_search_index_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = var.admin_object_id
}