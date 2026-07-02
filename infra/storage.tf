# ---------------------------------------------------------------------------
# Two storage accounts in the existing data RG:
#   - func: AzureWebJobsStorage + Durable Functions task hub state for the
#     indexing Function App (needs blob, queue, and table).
#   - data: source documents, chunks, reports, and saved intermediate state
#     for the indexing/query pipeline (blob only, organized by container).
# Both are private-endpoint-only (no public network access); DNS resolution
# for the privatelink zones is centrally managed (see AskUserQuestion answer
# in conversation - platform team owns Policy-based zone links).
# ---------------------------------------------------------------------------

resource "azurerm_storage_account" "func" {
  name                     = lower("corstfunccap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  public_network_access_enabled   = false
  allow_nested_items_to_be_public = false

  tags = local.common_tags
}

resource "azurerm_storage_account" "data" {
  name                     = lower("corstdatacap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  public_network_access_enabled   = false
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false # access via Azure AD / RBAC only

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.common_tags
}

resource "azurerm_storage_container" "documents" {
  name                  = "documents"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "chunks" {
  name                  = "chunks"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "reports" {
  name                  = "reports"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "state" {
  name                  = "state"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

resource "azurerm_private_endpoint" "stfunc" {
  name                = "cor-pep-stfunc-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-stfunc-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["blob", "queue", "table"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stdata" {
  name                = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.data.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}
