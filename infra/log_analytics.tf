resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-agentic-rag-chunking-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}