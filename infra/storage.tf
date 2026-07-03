# ---------------------------------------------------------------------------
# Two storage accounts in the existing data RG:
#   - func: AzureWebJobsStorage + Durable Functions task hub state for the
#     indexing Function App (needs blob, queue, and table), plus the file
#     share used for the Function App's WEBSITE_CONTENTAZUREFILECONNECTIONSTRING
#     content share (Elastic Premium always needs one; Azure Files/SMB has no
#     managed-identity auth, so this piece stays key-based - see
#     function_app.tf).
#   - data: source documents, chunks, reports, and saved intermediate state
#     for the indexing/query pipeline (blob only, organized by container).
# Both were designed private-endpoint-only, with DNS for the privatelink
# zones centrally managed by the platform team (Policy-based zone links).
# That DNS wiring isn't attached yet (docs/platform-team-dns-verzoek.md), so
# both accounts currently run on public endpoint + trusted-service-bypass
# firewall rules instead, as a temporary stand-in - see the per-resource
# comments below. Revert both to private-endpoint-only once that's fixed.
# ---------------------------------------------------------------------------

resource "azurerm_storage_account" "func" {
  name                     = lower("corstfunccap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # file is deliberately NOT private-endpoint-only (see removed
  # azurerm_private_endpoint.stfunc_file below): the content share needs the
  # missing privatelink.file.core.windows.net DNS zone group that the
  # platform team hasn't wired up yet (tracked in
  # docs/platform-team-dns-verzoek.md). Any private endpoint on a subresource
  # forces its public hostname into a CNAME to the (unreachable) privatelink
  # zone, which broke Kudu's content-share mount entirely. Trusted-service
  # bypass is Microsoft's documented pattern for exactly this case - the
  # Functions platform is on the trusted list, so it can still reach the
  # share even with public access closed to everyone else. Revert to
  # private-endpoint-only once the zone group exists.
  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }

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

  # Same exception as azurerm_storage_account.func, same reason: the blob PE
  # (azurerm_private_endpoint.stdata, commented out below) forces a CNAME to
  # privatelink.blob.core.windows.net with no DNS zone group attached yet
  # (docs/platform-team-dns-verzoek.md), which blocked the Function App from
  # reaching this account at all. Trusted-service bypass unblocks it in the
  # meantime; revert to private-endpoint-only once the zone group exists.
  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled left at its default (true): disabling it would
  # require the deploying identity to have Storage Blob Data Contributor
  # (data-plane RBAC, separate from Contributor) before Terraform can manage
  # containers via storage_use_azuread, which risks an RBAC-propagation race
  # on a fresh apply. Revisit once that identity's data-plane access is set up.

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }

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

# Azure Storage only permits one group Id per private endpoint for this
# account ("OnlyOneGroupIdPermitted... first-party resource"), so blob/queue/
# table each need their own private endpoint rather than one bundled PE.
resource "azurerm_private_endpoint" "stfunc_blob" {
  name                = "cor-pep-stfunc-blob-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-stfunc-blob-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stfunc_queue" {
  name                = "cor-pep-stfunc-queue-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-stfunc-queue-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["queue"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stfunc_table" {
  name                = "cor-pep-stfunc-table-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = "cor-pep-stfunc-table-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["table"]
    is_manual_connection           = false
  }

  tags = local.common_tags
}

# Commented out, not deleted: this PE is what broke the content-share mount
# (see the comment on azurerm_storage_account.func above / the
# public_network_access_enabled + network_rules exception on that resource).
# Any private endpoint on the "file" subresource forces its public hostname
# into a CNAME to privatelink.file.core.windows.net, which has no route
# without a DNS zone group the platform team hasn't attached yet (tracked in
# docs/platform-team-dns-verzoek.md). Re-enable this once that's fixed, and
# revert azurerm_storage_account.func back to private-endpoint-only.
# resource "azurerm_private_endpoint" "stfunc_file" {
#   name                = "cor-pep-stfunc-file-cap-${local.env}-${local.region}-${local.instance}"
#   location            = var.location
#   resource_group_name = data.azurerm_resource_group.data.name
#   subnet_id           = data.azurerm_subnet.pe.id
#
#   private_service_connection {
#     name                           = "cor-pep-stfunc-file-cap-${local.env}-${local.region}-${local.instance}-psc"
#     private_connection_resource_id = azurerm_storage_account.func.id
#     subresource_names              = ["file"]
#     is_manual_connection           = false
#   }
#
#   tags = local.common_tags
# }

# Commented out, not deleted: same DNS zone-group gap as stfunc_file above -
# this PE was blocking the Function App from reaching corstdatacapdevwe at
# all (401/network-unreachable, not an auth problem - see
# docs/platform-team-dns-verzoek.md). Re-enable once the platform team
# attaches the privatelink.blob.core.windows.net zone group, and revert
# azurerm_storage_account.data back to private-endpoint-only.
# resource "azurerm_private_endpoint" "stdata" {
#   name                = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}"
#   location            = var.location
#   resource_group_name = data.azurerm_resource_group.data.name
#   subnet_id           = data.azurerm_subnet.pe.id
#
#   private_service_connection {
#     name                           = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}-psc"
#     private_connection_resource_id = azurerm_storage_account.data.id
#     subresource_names              = ["blob"]
#     is_manual_connection           = false
#   }
#
#   tags = local.common_tags
# }
