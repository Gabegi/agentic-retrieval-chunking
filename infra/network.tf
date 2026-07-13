# ---------------------------------------------------------------------------
# Separate subnets for the compute tier's outbound VNet integration - one per
# workload rather than shared, so routing/NSG changes for one (e.g. the
# Function App's content-share carve-out below) can't accidentally affect the
# other's blast radius. Both still delegate to Microsoft.Web/serverFarms.
# Private endpoints (inbound, and for Search/Storage/Key Vault) stay in the
# existing data.azurerm_subnet.pe - only outbound egress needs its own
# delegated subnet, since delegation is exclusive per subnet.
# ---------------------------------------------------------------------------

# One subnet + NSG + pair of associations per workload, looped via for_each
# rather than copy-pasted per workload - both entries are otherwise identical
# (same delegation, same "deny internet inbound" NSG rule), differing only in
# name and address space. Add a workload by adding a map entry, not by
# copying a block. Renaming .func/.api to .workload["func"/"api"] below is a
# pure Terraform-address move (name/address_prefixes/rule unchanged) - the
# moved blocks make it a no-op against the real Azure resources.
locals {
  workload_subnets = {
    func = "10.243.5.0/24"
    api  = "10.243.6.0/24"
  }
}

moved {
  from = azurerm_subnet.func
  to   = azurerm_subnet.workload["func"]
}

moved {
  from = azurerm_subnet.api
  to   = azurerm_subnet.workload["api"]
}

moved {
  from = azurerm_network_security_group.func
  to   = azurerm_network_security_group.workload["func"]
}

moved {
  from = azurerm_network_security_group.api
  to   = azurerm_network_security_group.workload["api"]
}

moved {
  from = azurerm_subnet_network_security_group_association.func
  to   = azurerm_subnet_network_security_group_association.workload["func"]
}

moved {
  from = azurerm_subnet_network_security_group_association.api
  to   = azurerm_subnet_network_security_group_association.workload["api"]
}

moved {
  from = azurerm_subnet_route_table_association.func
  to   = azurerm_subnet_route_table_association.workload["func"]
}

moved {
  from = azurerm_subnet_route_table_association.api
  to   = azurerm_subnet_route_table_association.workload["api"]
}

resource "azurerm_subnet" "workload" {
  for_each             = local.workload_subnets
  name                 = "cor-snet-cap-${each.key}-${local.instance}"
  resource_group_name  = data.azurerm_resource_group.network.name
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = [each.value]

  delegation {
    name = "webapp-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

# Separate NSGs per subnet (rather than one shared NSG) so rule changes for
# one workload can't accidentally affect the other's blast radius. Both start
# from the same "nothing should ever originate inbound here" rule: outbound-
# only VNet integration for App Service-family compute, inbound to either app
# goes through its own private endpoint in the separate PE subnet, not here.
resource "azurerm_network_security_group" "workload" {
  for_each            = local.workload_subnets
  name                = "cor-nsg-${each.key}-cap-${local.env}-${local.region}-${local.instance}"
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

resource "azurerm_subnet_network_security_group_association" "workload" {
  for_each                   = local.workload_subnets
  subnet_id                  = azurerm_subnet.workload[each.key].id
  network_security_group_id = azurerm_network_security_group.workload[each.key].id
}

resource "azurerm_subnet_route_table_association" "workload" {
  for_each       = local.workload_subnets
  subnet_id      = azurerm_subnet.workload[each.key].id
  route_table_id = data.azurerm_route_table.spoke.id
}
