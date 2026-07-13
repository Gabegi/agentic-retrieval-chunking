# ---------------------------------------------------------------------------
# Separate subnets for the compute tier's outbound VNet integration - one per
# workload rather than shared, so routing/NSG changes for one (e.g. the
# Function App's content-share carve-out below) can't accidentally affect the
# other's blast radius. Both still delegate to Microsoft.Web/serverFarms.
# Private endpoints (inbound, and for Search/Storage/Key Vault) stay in the
# existing data.azurerm_subnet.pe - only outbound egress needs its own
# delegated subnet, since delegation is exclusive per subnet.
# ---------------------------------------------------------------------------

# PHASE 2: cor-snet-cap-app-001 (and the Function App attached to it) were
# destroyed in phase 1, so this is a plain fresh create now, not a rename -
# no moved block needed, there's nothing left in state to move from.
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

# Separate NSGs per subnet (rather than the shared one this replaced) so
# rule changes for one workload can't accidentally affect the other's blast
# radius - same reasoning as the subnet split above. Both still start from
# the same "nothing should ever originate inbound here" rule: outbound-only
# VNet integration for App Service-family compute, inbound to either app
# goes through its own private endpoint in the separate PE subnet, not here.
resource "azurerm_network_security_group" "api" {
  name                = "cor-nsg-api-cap-${local.env}-${local.region}-${local.instance}"
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

resource "azurerm_network_security_group" "func" {
  name                = "cor-nsg-func-cap-${local.env}-${local.region}-${local.instance}"
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
  network_security_group_id = azurerm_network_security_group.func.id
}

resource "azurerm_subnet_network_security_group_association" "api" {
  subnet_id                 = azurerm_subnet.api.id
  network_security_group_id = azurerm_network_security_group.api.id
}

resource "azurerm_subnet_route_table_association" "func" {
  subnet_id      = azurerm_subnet.func.id
  route_table_id = data.azurerm_route_table.spoke.id
}

resource "azurerm_subnet_route_table_association" "api" {
  subnet_id      = azurerm_subnet.api.id
  route_table_id = data.azurerm_route_table.spoke.id
}
