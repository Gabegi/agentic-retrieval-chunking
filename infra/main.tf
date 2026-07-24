locals {
  common_tags = merge(var.tags, {
    environment = var.environment
    project     = var.project
    managed_by  = "terraform"
  })
}
