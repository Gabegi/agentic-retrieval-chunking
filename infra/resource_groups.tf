# ---------------------------------------------------------------------------
# Resource groups owned/created by this Terraform config (as opposed to the
# network/ai/data RGs in data.tf, which are owned by the platform team).
# ---------------------------------------------------------------------------

# DISABLED: exists only to host the query API App Service (app_service.tf),
# which isn't being built yet. Re-enable both together.
# resource "azurerm_resource_group" "api" {
#   name     = "cor-cap-api-${local.env}-${local.region}-${local.instance}"
#   location = var.location
#   tags     = local.common_tags
# }
