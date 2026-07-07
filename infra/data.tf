# ---------------------------------------------------------------------------
# Existing landing zone resources, referenced read-only via data sources.
# Nothing in this file is created or modified by this Terraform config.
# ---------------------------------------------------------------------------

# --- Networking (owned by the platform/network team) ------------------------

data "azurerm_resource_group" "network" {
  name = "cor-cap-network-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_virtual_network" "main" {
  name                = "cor-vnet-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_subnet" "pe" {
  name                 = "cor-snet-cap-pe-${local.instance}"
  virtual_network_name = data.azurerm_virtual_network.main.name
  resource_group_name  = data.azurerm_resource_group.network.name
}

data "azurerm_network_security_group" "pe" {
  name                = "cor-nsg-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_route_table" "spoke" {
  name                = "cor-rt-spoke-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

# --- Foundry / AI -------------------------------------------------------------

data "azurerm_resource_group" "ai" {
  name = "cor-cap-ai-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_cognitive_account" "foundry" {
  name                = "cor-ais-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

data "azurerm_application_insights" "foundry" {
  name                = "cor-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# No data source exists for azurerm_private_endpoint - it's resource-only in
# this provider. The PE's NIC is readable directly since its name is
# deterministic ("<private endpoint name>_nic").
data "azurerm_network_interface" "ai_services_pe" {
  name                = "cor-pep-ais-cap-${local.env}-${local.region}-${local.instance}_nic"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# --- Data tier ------------------------------------------------------------

data "azurerm_resource_group" "data" {
  name = "cor-cap-data-${local.env}-${local.region}-${local.instance}"
}

# --- Private DNS zones (hub, owned by platform team) -----------------------
# Subscription cor-connectivity-prd, RG cor-connectivity-dns-prd-we-001.
# Confirmed via pipeline diagnostic (2026-07-07): the SP has Private DNS
# Zone Contributor scoped individually on these 4 zones, so their private
# endpoints attach a private_dns_zone_group directly (search.tf, storage.tf,
# keyvault.tf, function_app.tf) rather than waiting on the platform team's
# policy-based remediation. privatelink.queue/table.core.windows.net and
# privatelink.search.windows.net still don't exist in the hub at all
# (confirmed 2026-07-07, same diagnostic) - that's a bigger ask than linking
# (docs/platform-team-dns-verzoek.md), so no data source for them yet; the
# stfunc_queue/stfunc_table/search private endpoints stay without a zone
# group until the platform team creates those zones.

data "azurerm_private_dns_zone" "azurewebsites" {
  provider            = azurerm.hub
  name                = "privatelink.azurewebsites.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "blob" {
  provider            = azurerm.hub
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "file" {
  provider            = azurerm.hub
  name                = "privatelink.file.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "vaultcore" {
  provider            = azurerm.hub
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "queue" {
  provider            = azurerm.hub
  name                = "privatelink.queue.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "table" {
  provider            = azurerm.hub
  name                = "privatelink.table.core.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "search" {
  provider            = azurerm.hub
  name                = "privatelink.search.windows.net"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

# Not currently consumed by any private endpoint in this repo (the Foundry
# account is referenced via data.azurerm_cognitive_account.foundry, managed
# elsewhere) - captured here since the diagnostic surfaced them, in case
# they're needed later.
data "azurerm_private_dns_zone" "cognitiveservices" {
  provider            = azurerm.hub
  name                = "privatelink.cognitiveservices.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "openai" {
  provider            = azurerm.hub
  name                = "privatelink.openai.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "services_ai" {
  provider            = azurerm.hub
  name                = "privatelink.services.ai.azure.com"
  resource_group_name = "cor-connectivity-dns-prd-we-001"
}
