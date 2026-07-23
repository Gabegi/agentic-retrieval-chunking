# ---------------------------------------------------------------------------
# TEMPORARY diagnostic VM - prove from inside cor-vnet-cap-dev-we-001 whether
# DNS resolution for cor-ais-cap-dev-we-001.cognitiveservices.azure.com /
# .openai.azure.com returns a private (10.243.4.x) or public IP, to
# confirm/refute the missing private-DNS-zone-VNet-link theory behind
# ExtractActivity's 403s (PdfIndexingFunction.cs:148).
#
# No public IP - reachable only via `az vm run-command invoke` (control-plane
# channel, no network path needed from the operator's machine). Delete this
# file and run `terraform apply` again once the test is done; nothing here is
# meant to persist.
# ---------------------------------------------------------------------------

resource "azurerm_resource_group" "dbgdns" {
  name     = "cor-cap-dbgdns-${local.env}-${local.region}-${local.instance}"
  location = var.location
  tags     = local.common_tags
}

# 10.243.7.0/28, inside the existing VNet's 10.243.4.0/22 space - free per
# `az network vnet show` (pe=10.243.4.0/26, func=10.243.5.0/24,
# api=10.243.6.0/24). Not delegated (unlike cor-snet-cap-func-${instance},
# which is delegated to Microsoft.Web/serverFarms and would reject a VM NIC).
# Private DNS zone VNet links apply at the VNet level, so subnet choice
# doesn't affect the DNS test.
resource "azurerm_subnet" "dbgdns" {
  name                 = "cor-snet-cap-dbgdns-${local.instance}"
  resource_group_name  = data.azurerm_resource_group.network.name
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = ["10.243.7.0/28"]
}

resource "azurerm_subnet_route_table_association" "dbgdns" {
  subnet_id      = azurerm_subnet.dbgdns.id
  route_table_id = data.azurerm_route_table.spoke.id
}

resource "azurerm_network_security_group" "dbgdns" {
  name                = "cor-nsg-dbgdns-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.dbgdns.name
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
  # Outbound left at NSG default-allow deliberately - the whole point is to
  # observe where DNS/HTTPS egress actually goes (private endpoint vs public).
}

resource "azurerm_subnet_network_security_group_association" "dbgdns" {
  subnet_id                 = azurerm_subnet.dbgdns.id
  network_security_group_id = azurerm_network_security_group.dbgdns.id
}

resource "azurerm_network_interface" "dbgdns" {
  name                = "cor-nic-dbgdns-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.dbgdns.name
  tags                = local.common_tags

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.dbgdns.id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_linux_virtual_machine" "dbgdns" {
  name                = "cor-vm-dbgdns-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.dbgdns.name
  size                = "Standard_B1s"
  admin_username      = "azureuser"
  network_interface_ids = [
    azurerm_network_interface.dbgdns.id,
  ]

  # Password auth only because this VM is reached exclusively through
  # `az vm run-command invoke` (control-plane, not SSH) - no public IP, no
  # inbound NSG rule, so this credential is never actually usable over the
  # network. Satisfies the resource's required auth block only.
  disable_password_authentication = false
  admin_password                  = "Tmp-DbgDns-2026!VM"

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "Standard_LRS"
  }

  source_image_reference {
    publisher = "Canonical"
    offer     = "0001-com-ubuntu-server-jammy"
    sku       = "22_04-lts-gen2"
    version   = "latest"
  }

  tags = local.common_tags
}

output "dbgdns_vm_name" {
  value = azurerm_linux_virtual_machine.dbgdns.name
}

output "dbgdns_run_command_example" {
  value = "az vm run-command invoke -g ${azurerm_resource_group.dbgdns.name} -n ${azurerm_linux_virtual_machine.dbgdns.name} --command-id RunShellScript --scripts \"nslookup cor-ais-cap-dev-we-001.cognitiveservices.azure.com; nslookup cor-ais-cap-dev-we-001.openai.azure.com; curl -sk -o /dev/null -w 'HTTP %%{http_code}\\n' https://cor-ais-cap-dev-we-001.cognitiveservices.azure.com/\""
}
