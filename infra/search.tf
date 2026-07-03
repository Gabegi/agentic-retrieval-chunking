resource "azurerm_search_service" "main" {
  name                = "cor-srch-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  sku                 = "standard"

  public_network_access_enabled = false

  identity {
    type = "SystemAssigned"
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "search" {
  name                          = "cor-pep-srch-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-srch-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-srch-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_search_service.main.id
    subresource_names              = ["searchService"]
    is_manual_connection           = false
  }

  # No private_dns_zone_group here yet: privatelink.search.windows.net
  # doesn't exist in the hub at all (cor-connectivity-dns-prd-we-001) - the
  # platform team needs to create the zone first, not just link/attach it
  # (docs/platform-team-dns-verzoek.md).

  tags = local.common_tags
}
