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

  # Commented out until the platform team creates privatelink.search.windows.net
  # in the hub - confirmed 2026-07-07 it doesn't exist there at all yet (not
  # just an unlinked zone). Uncomment once it exists (data.tf).
  # private_dns_zone_group {
  #   name                 = "default"
  #   private_dns_zone_ids = [data.azurerm_private_dns_zone.search.id]
  # }

  tags = local.common_tags
}
