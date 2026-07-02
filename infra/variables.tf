variable "environment" {
  type        = string
  description = "Environment name (dev, prod)"
}

variable "location" {
  type        = string
  description = "Azure region"
}

variable "project" {
  type        = string
  description = "Project name used in resource naming"
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to all resources"
  default     = {}
}

variable "openai_embedding_deployment" {
  type        = string
  description = "Deployment name for the text-embedding-3-large model on the Foundry AI Services account"
  default     = "embedding-3-large"
}

variable "openai_gpt_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the query API"
  default     = "gpt-4.1-query"
}

variable "openai_extraction_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the indexing/extraction pipeline"
  default     = "gpt-4.1-extraction"
}

variable "openai_eval_deployment" {
  type        = string
  description = "Deployment name for the gpt-4o model used for evaluation"
  default     = "gpt-4o-eval"
}
