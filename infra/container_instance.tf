resource "azurerm_user_assigned_identity" "aci_indexer" {
  name                = "mi-protocols-indexer-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
}

# ACR pull
resource "azurerm_role_assignment" "aci_indexer_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}

# Storage
resource "azurerm_role_assignment" "aci_indexer_blob_reader" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}

# AI Search
resource "azurerm_role_assignment" "aci_indexer_search_index_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}

resource "azurerm_role_assignment" "aci_indexer_search_service_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Service Contributor"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}

# Azure OpenAI
resource "azurerm_role_assignment" "aci_indexer_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}

resource "azurerm_container_group" "invoice_indexer" {
  name                = "aci-protocols-indexer-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  os_type             = "Linux"
  restart_policy      = "Never"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.aci_indexer.id]
  }

  image_registry_credential {
    server                    = azurerm_container_registry.main.login_server
    user_assigned_identity_id = azurerm_user_assigned_identity.aci_indexer.id
  }

  container {
    name   = "protocols-indexer"
    image  = "${azurerm_container_registry.main.login_server}/protocols-indexer:${var.indexer_image_tag}"
    cpu    = "2"
    memory = "4"

    ports {
      port     = 80
      protocol = "TCP"
    }

    environment_variables = {
      SEARCH_ENDPOINT                = "https://${azurerm_search_service.main.name}.search.windows.net"
      OPENAI_ENDPOINT                = "https://${azurerm_cognitive_account.openai.custom_subdomain_name}.openai.azure.com/"
      OPENAI_EMBEDDING_DEPLOYMENT    = var.openai_embedding_deployment
      OPENAI_GPT_DEPLOYMENT          = var.openai_gpt_deployment
      OPENAI_GPT_MODEL_NAME          = var.openai_gpt_model_name
      OPENAI_EXTRACTION_DEPLOYMENT   = var.openai_extraction_deployment
      STORAGE_ACCOUNT_URL            = azurerm_storage_account.documents.primary_blob_endpoint
      STORAGE_CONTAINER              = azurerm_storage_container.documents.name
      SEARCH_INDEX_NAME              = var.search_index_name
      KNOWLEDGE_SOURCE_NAME          = var.knowledge_source_name
      KNOWLEDGE_BASE_NAME            = var.knowledge_base_name
      AZURE_CLIENT_ID                = azurerm_user_assigned_identity.aci_indexer.client_id
    }
  }

  diagnostics {
    log_analytics {
      workspace_id  = azurerm_log_analytics_workspace.main.workspace_id
      workspace_key = azurerm_log_analytics_workspace.main.primary_shared_key
    }
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }

  depends_on = [
    azurerm_role_assignment.aci_indexer_acr_pull,
    azurerm_role_assignment.aci_indexer_blob_reader,
    azurerm_role_assignment.aci_indexer_search_index_contributor,
    azurerm_role_assignment.aci_indexer_search_service_contributor,
    azurerm_role_assignment.aci_indexer_openai_user,
  ]

  # Prevent infra pipeline from recreating the container — image updates are managed by deploy-protocols-indexer
  lifecycle {
    ignore_changes = [container]
  }
}