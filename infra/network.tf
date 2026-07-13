# ---------------------------------------------------------------------------
# Separate subnets for the compute tier's outbound VNet integration - one per
# workload rather than shared, so routing/NSG changes for one (e.g. the
# Function App's content-share carve-out below) can't accidentally affect the
# other's blast radius. Both still delegate to Microsoft.Web/serverFarms.
# Private endpoints (inbound, and for Search/Storage/Key Vault) stay in the
# existing data.azurerm_subnet.pe - only outbound egress needs its own
# delegated subnet, since delegation is exclusive per subnet.
# ---------------------------------------------------------------------------

# Renamed from azurerm_subnet.app - same address space, same underlying
# subnet, just no longer shared with the API.
moved {
  from = azurerm_subnet.app
  to   = azurerm_subnet.func
}

moved {
  from = azurerm_subnet_network_security_group_association.app
  to   = azurerm_subnet_network_security_group_association.func
}

moved {
  from = azurerm_subnet_route_table_association.app
  to   = azurerm_subnet_route_table_association.func
}

resource "azurerm_subnet" "func" {
  name                 = "cor-snet-cap-func-${local.instance}"
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

resource "azurerm_subnet" "api" {
  name                 = "cor-snet-cap-api-${local.instance}"
  resource_group_name  = data.azurerm_resource_group.network.name
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = ["10.243.6.0/24"]

  delegation {
    name = "webapp-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

# Shared rule, shared NSG - both subnets are outbound-only VNet integration
# for App Service-family compute, so the "nothing should ever originate
# inbound here" intent is identical for both. Inbound to either app goes
# through its own private endpoint in the separate PE subnet, not here.
resource "azurerm_network_security_group" "app" {
  name                = "cor-nsg-app-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.network.name
  tags                = local.common_tags

  security_rule {
    name                       = "DenyInternetInbound"
    priority                   = 200
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "func" {
  subnet_id                 = azurerm_subnet.func.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_subnet_network_security_group_association" "api" {
  subnet_id                 = azurerm_subnet.api.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_subnet_route_table_association" "func" {
  subnet_id      = azurerm_subnet.func.id
  route_table_id = data.azurerm_route_table.spoke.id
}

resource "azurerm_subnet_route_table_association" "api" {
  subnet_id      = azurerm_subnet.api.id
  route_table_id = data.azurerm_route_table.spoke.id
}

# Regional VNet Integration + vnet_route_all_enabled (function_app.tf) sends
# ALL egress through this route table, even to destinations in the same VNet -
# unlike a normal NIC, which would prefer the more-specific system route to a
# sibling subnet. Without this, traffic to the pe subnet's private endpoints
# hairpins through the hub firewall via the 0.0.0.0/0 default route, which
# blocks SMB/445 and breaks the Function App's content-share mount. This
# route's prefix is more specific, so it wins for that destination while
# everything else still goes to the firewall.
resource "azurerm_route" "pe_subnet_local" {
  name                = "pe-subnet-local"
  resource_group_name = data.azurerm_resource_group.network.name
  route_table_name    = data.azurerm_route_table.spoke.name
  address_prefix      = data.azurerm_subnet.pe.address_prefixes[0]
  next_hop_type       = "VnetLocal"
}
