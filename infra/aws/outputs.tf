output "aws_region" {
  value = var.aws_region
}

output "export_bucket" {
  value = aws_s3_bucket.exports.bucket
}

output "queue_url" {
  value = aws_sqs_queue.jobs.url
}

output "dead_letter_queue_url" {
  value = aws_sqs_queue.dead_letter.url
}

output "ecr_repositories" {
  value = {
    api    = aws_ecr_repository.api.repository_url
    worker = aws_ecr_repository.worker.repository_url
    portal = aws_ecr_repository.portal.repository_url
  }
}

output "secret_names" {
  value = {
    mongo_connection     = aws_secretsmanager_secret.mongo_connection.name
    api_key              = aws_secretsmanager_secret.api_key.name
    download_signing_key = aws_secretsmanager_secret.download_signing_key.name
    portal_password      = aws_secretsmanager_secret.portal_password.name
  }
}

output "api_url" {
  value = var.deploy_app ? "https://${local.api_domain}" : null
}

output "acm_validation_records" {
  description = "Crie estes CNAMEs na Cloudflare com proxy DNS only e mantenha-os para renovação do certificado."
  value = {
    for option in aws_acm_certificate.web.domain_validation_options : option.domain_name => {
      type   = option.resource_record_type
      name   = option.resource_record_name
      target = option.resource_record_value
    }
  }
}

output "acm_certificate_arn" {
  value = aws_acm_certificate.web.arn
}

output "alb_dns_name" {
  description = "Destino dos CNAMEs apiga e ga na Cloudflare após a segunda fase."
  value       = var.deploy_app ? aws_lb.web[0].dns_name : null
}

output "portal_url" {
  value = var.deploy_app ? "https://${local.portal_domain}" : null
}
