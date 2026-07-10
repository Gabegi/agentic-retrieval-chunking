# ---------------------------------------------------------------------------
# Windows Function App (dotnet-isolated, EP1 Premium) - durable indexing
# pipeline. Reuses the storage account, App Insights, Search, and Foundry
# resources already defined elsewhere rather than provisioning its own.
# VNet-integrated into the shared app subnet (outbound) with a private
# endpoint (inbound), matching the hub-firewall-routed architecture.
# ---------------------------------------------------------------------------

resource "azurerm_service_plan" "func" {
  name                = "cor-plan-func-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.data.name
  location            = var.location
  os_type             = "Windows"
  # Elastic Premium, not P1v3 - matches the earlier decision to run the
  # durable indexing pipeline on a Premium (Elastic) plan.
  sku_name = "EP1"

  tags = local.common_tags
}

resource "azurerm_windows_function_app" "indexer" {
  name                          = "cor-func-idx-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name           = data.azurerm_resource_group.data.name
  location                      = var.location
  service_plan_id               = azurerm_service_plan.func.id
  storage_account_name          = azurerm_storage_account.func.name
  storage_uses_managed_identity = true
  virtual_network_subnet_id     = azurerm_subnet.app.id
  # Deny-by-default public access (no ip_restriction/scm_ip_restriction rules
  # managed here), so the private endpoint stays the only stable path in.
  # The app-deploy pipeline runs on a Microsoft-hosted agent with no VNet
  # access, so it opens a scoped Allow rule on the SCM site for its own
  # runner IP via `az functionapp config access-restriction add`
  # immediately before the zip deploy, then removes it again immediately
  # after - see 4-deploy-application.yml.
  public_network_access_enabled = true
  # storage_uses_managed_identity only covers AzureWebJobsStorage/Durable
  # Functions (blob/queue/table). The EP1 plan's content share still needs a
  # key-based connection string - Azure Files/SMB has no managed-identity
  # auth path - plus WEBSITE_CONTENTOVERVNET so the platform reaches it via
  # the private endpoint (azurerm_private_endpoint.stfunc_file in storage.tf)
  # instead of the public endpoint.

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version              = "v8.0"
      use_dotnet_isolated_runtime = true
    }
    always_on                              = true
    vnet_route_all_enabled                 = true
    application_insights_connection_string = data.azurerm_application_insights.foundry.connection_string
    ip_restriction_default_action          = "Deny"
    scm_ip_restriction_default_action      = "Deny"

    cors {
      allowed_origins = ["https://portal.azure.com"]
    }
  }

  app_settings = {
    "FUNCTIONS_WORKER_RUNTIME"              = "dotnet-isolated"
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = data.azurerm_application_insights.foundry.connection_string
    # Durable Functions managed-identity auth - no connection string needed
    "AzureWebJobsStorage__accountName" = azurerm_storage_account.func.name
    "AzureWebJobsStorage__credential"  = "managedidentity"
    # Content share: key-based (see note above azurerm_windows_function_app.indexer)
    "WEBSITE_CONTENTOVERVNET"                  = "1"
    "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING" = azurerm_storage_account.func.primary_connection_string
    "WEBSITE_CONTENTSHARE"                     = "cor-func-idx-cap-${local.env}-${local.region}-${local.instance}"
    # WEBSITE_CONTENTOVERVNET alone doesn't make the site's own DNS resolution
    # (used by Kudu to resolve the content share's *.file.core.windows.net)
    # honor the VNet-linked private DNS zone - that needs this resolver
    # explicitly, or it falls back to public DNS and hits the storage
    # account's public endpoint, which public_network_access_enabled = false
    # on azurerm_storage_account.func then rejects.
    "ProtocolsStorage__blobServiceUri"         = azurerm_storage_account.data.primary_blob_endpoint
    "STORAGE_ACCOUNT_URL"                      = azurerm_storage_account.data.primary_blob_endpoint
    "SEARCH_ENDPOINT"                          = "https://${azurerm_search_service.main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                          = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_EMBEDDING_DEPLOYMENT"              = var.openai_embedding_deployment
    "OPENAI_GPT_DEPLOYMENT"                    = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"                    = var.openai_gpt_model_name
    "OPENAI_EXTRACTION_DEPLOYMENT"             = var.openai_extraction_deployment
    "SEARCH_INDEX_NAME"                        = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"                    = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"                      = var.knowledge_base_name
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "func" {
  name                          = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_windows_function_app.indexer.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.azurewebsites.id]
  }

  tags = local.common_tags
}

# Temporary blob storage for large Durable payloads (extracted docs + chunks
# between activities) - lives on the function's own storage, not the shared
# data storage account.
resource "azurerm_storage_container" "indexing_pipeline" {
  name                  = "indexing-pipeline"
  storage_account_id    = azurerm_storage_account.func.id
  container_access_type = "private"
}

# --- Role assignments -------------------------------------------------------

resource "azurerm_role_assignment" "func_storage_owner" {
  scope                = azurerm_storage_account.func.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

# Durable Functions store orchestration state in queues and tables
resource "azurerm_role_assignment" "func_storage_queue_contributor" {
  scope                = azurerm_storage_account.func.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_storage_table_contributor" {
  scope                = azurerm_storage_account.func.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

# Reads source documents, writes chunks/reports/state back to the data
# storage account.
resource "azurerm_role_assignment" "func_data_storage_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_search_index_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_search_service_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Service Contributor"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_openai_user" {
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}
