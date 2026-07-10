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

# Regional VNet Integration + vnet_route_all_enabled (function_app.tf) sends
# ALL egress from the app subnet through this route table's rules, even to
# destinations inside the same VNet - unlike a normal NIC, which would prefer
# the more-specific implicit system route to a sibling subnet. Without this,
# traffic to the private endpoints in the pe subnet (storage, search, key
# vault, the Function App's own inbound PE) follows the existing
# 0.0.0.0/0 default-to-firewall route and hairpins through the hub firewall,
# which blocks SMB/445 - breaking the Function App's own content-share mount
# (see storage.tf, function_app.tf). This route is more specific than that
# default route, so it wins for the pe subnet's prefix while every other
# destination still goes to the firewall for inspection.
resource "azurerm_route" "pe_subnet_local" {
  name                   = "pe-subnet-local"
  resource_group_name    = data.azurerm_resource_group.network.name
  route_table_name       = data.azurerm_route_table.spoke.name
  address_prefix         = data.azurerm_subnet.pe.address_prefixes[0]
  next_hop_type          = "VnetLocal"
}
