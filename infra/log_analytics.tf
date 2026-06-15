resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-support-agent-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  tags = {
    project     = "support-agent"
    environment = "dev"
  }
}