# function_app.tf
# Windows Function App (zip deploy, Consumption plan) for the protocols indexer

resource "azurerm_storage_account" "func_indexer" {
  name                     = "stprotocolindexerfn"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_application_insights" "func_indexer" {
  name                = "appi-protocols-indexer"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_service_plan" "func_indexer" {
  name                = "asp-protocols-indexer"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  os_type             = "Windows"
  sku_name            = "B1"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_windows_function_app" "protocols_indexer" {
  name                          = "func-protocols-indexer"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  service_plan_id               = azurerm_service_plan.func_indexer.id
  storage_account_name          = azurerm_storage_account.func_indexer.name
  storage_uses_managed_identity = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version              = "v8.0"
      use_dotnet_isolated_runtime = true
    }
    application_insights_connection_string = azurerm_application_insights.func_indexer.connection_string
    cors {
      allowed_origins = ["https://portal.azure.com"]
    }
  }

  app_settings = {
    "FUNCTIONS_WORKER_RUNTIME"              = "dotnet-isolated"
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.func_indexer.connection_string
    # Durable Functions managed-identity auth — no connection string needed
    "AzureWebJobsStorage__accountName" = azurerm_storage_account.func_indexer.name
    "AzureWebJobsStorage__credential"  = "managedidentity"
    "ProtocolsStorage__blobServiceUri" = azurerm_storage_account.documents.primary_blob_endpoint
    "STORAGE_ACCOUNT_URL"              = azurerm_storage_account.documents.primary_blob_endpoint
    "SEARCH_ENDPOINT"                  = "https://${azurerm_search_service.main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                  = "https://${azurerm_cognitive_account.openai.custom_subdomain_name}.openai.azure.com/"
    "OPENAI_EMBEDDING_DEPLOYMENT"      = var.openai_embedding_deployment
    "OPENAI_GPT_DEPLOYMENT"            = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"            = var.openai_gpt_model_name
    "OPENAI_EXTRACTION_DEPLOYMENT"     = var.openai_extraction_deployment
    "SEARCH_INDEX_NAME"                = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"            = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"              = var.knowledge_base_name
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }

  lifecycle {
    ignore_changes = [tags]
  }
}

# Temporary blob storage for large Durable payloads (extracted docs + chunks between activities)
resource "azurerm_storage_container" "indexing_pipeline" {
  name                  = "indexing-pipeline"
  storage_account_id    = azurerm_storage_account.func_indexer.id
  container_access_type = "private"
}

# ── Role assignments ──────────────────────────────────────────────────────────

resource "azurerm_role_assignment" "func_indexer_storage_owner" {
  scope                = azurerm_storage_account.func_indexer.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

# Durable Functions store orchestration state in queues and tables
resource "azurerm_role_assignment" "func_indexer_storage_queue_contributor" {
  scope                = azurerm_storage_account.func_indexer.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_storage_table_contributor" {
  scope                = azurerm_storage_account.func_indexer.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_blob_reader" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_search_index_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_search_service_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Service Contributor"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_indexer_document_intelligence" {
  scope                = azurerm_cognitive_account.document_intelligence.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_windows_function_app.protocols_indexer.identity[0].principal_id
}
