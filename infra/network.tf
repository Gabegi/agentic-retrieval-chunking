# ---------------------------------------------------------------------------
# New subnet for the compute tier's outbound VNet integration. Function App
# (Premium) and App Service (Premium v3) both delegate to
# Microsoft.Web/serverFarms, so they can share this one subnet. Private
# endpoints (inbound, and for Search/Storage/Key Vault) stay in the existing
# data.azurerm_subnet.pe - only outbound egress needs its own delegated
# subnet, since delegation is exclusive per subnet.
# ---------------------------------------------------------------------------

resource "azurerm_subnet" "app" {
  name                 = "cor-snet-cap-app-${local.instance}"
  resource_group_name  = data.azurerm_resource_group.network.name
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = ["10.243.5.0/24"]

  delegation {
    name = "webapp-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

resource "azurerm_network_security_group" "app" {
  name                = "cor-nsg-app-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.network.name
  tags                = local.common_tags
}

resource "azurerm_subnet_network_security_group_association" "app" {
  subnet_id                 = azurerm_subnet.app.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_subnet_route_table_association" "app" {
  subnet_id      = azurerm_subnet.app.id
  route_table_id = data.azurerm_route_table.spoke.id
}
