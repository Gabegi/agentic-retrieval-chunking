# ---------------------------------------------------------------------------
# Linux App Service (.NET 10, Premium v3) - query API in front of Azure AI
# Search + the Foundry GPT deployment.
# ---------------------------------------------------------------------------

resource "azurerm_service_plan" "api" {
  name                = "cor-plan-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = azurerm_resource_group.api.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "P1v3"

  tags = local.common_tags
}

resource "azurerm_linux_web_app" "query" {
  name                           = "cor-app-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name            = azurerm_resource_group.api.name
  location                       = var.location
  service_plan_id                = azurerm_service_plan.api.id
  virtual_network_subnet_id      = azurerm_subnet.api.id
  public_network_access_enabled  = false
  https_only                     = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version = "10.0"
    }
    always_on              = true
    vnet_route_all_enabled = true
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = data.azurerm_application_insights.main.connection_string
    "SEARCH_ENDPOINT"                       = "https://${azurerm_search_service.foundry_iq_main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                       = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_GPT_DEPLOYMENT"                 = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"                 = var.openai_gpt_model_name
    "SEARCH_INDEX_NAME"                     = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"                 = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"                   = var.knowledge_base_name
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "api" {
  name                = "cor-pep-api-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.api.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-api-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_linux_web_app.query.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}

# --- Role assignments -------------------------------------------------------
# Read-only on Search (query only, no index management) and OpenAI calls for
# RAG generation. No storage access yet - add if the API ends up fetching raw
# source documents rather than just querying the index.

resource "azurerm_role_assignment" "api_search_index_reader" {
  scope                = azurerm_search_service.foundry_iq_main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = azurerm_linux_web_app.query.identity[0].principal_id
}

resource "azurerm_role_assignment" "api_openai_user" {
  scope                = data.azurerm_cognitive_account_project.rag.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_linux_web_app.query.identity[0].principal_id
}
