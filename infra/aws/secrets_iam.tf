data "aws_iam_policy_document" "ecs_assume_role" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Somente os contêineres leem os valores. Terraform cria metadados, sem gravar senhas no state.
resource "aws_secretsmanager_secret" "mongo_connection" {
  name                    = "${local.name}/mongo-connection"
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret" "api_key" {
  name                    = "${local.name}/api-key"
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret" "download_signing_key" {
  name                    = "${local.name}/download-signing-key"
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret" "portal_password" {
  name                    = "${local.name}/portal-password"
  recovery_window_in_days = 7
}

resource "aws_iam_role" "execution" {
  name               = "${local.name}-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume_role.json
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "execution_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = concat([
      aws_secretsmanager_secret.mongo_connection.arn,
      aws_secretsmanager_secret.api_key.arn,
      aws_secretsmanager_secret.download_signing_key.arn,
      aws_secretsmanager_secret.portal_password.arn,
    ], var.resend_secret_arn == null ? [] : [var.resend_secret_arn])
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  name   = "${local.name}-read-secrets"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_secrets.json
}

resource "aws_iam_role" "api" {
  name               = "${local.name}-api"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume_role.json
}

data "aws_iam_policy_document" "api" {
  statement {
    actions   = ["sqs:SendMessage"]
    resources = [aws_sqs_queue.jobs.arn]
  }
  statement {
    actions   = ["s3:GetObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.exports.arn}/arquivos/*"]
  }
}

resource "aws_iam_role_policy" "api" {
  name   = "${local.name}-api-access"
  role   = aws_iam_role.api.id
  policy = data.aws_iam_policy_document.api.json
}

resource "aws_iam_role" "worker" {
  name               = "${local.name}-worker"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume_role.json
}

data "aws_iam_policy_document" "worker" {
  statement {
    actions = [
      "sqs:ReceiveMessage",
      "sqs:DeleteMessage",
      "sqs:GetQueueAttributes",
    ]
    resources = [aws_sqs_queue.jobs.arn]
  }
  statement {
    actions = [
      "s3:PutObject",
      "s3:DeleteObject",
      "s3:AbortMultipartUpload",
      "s3:ListMultipartUploadParts",
    ]
    resources = ["${aws_s3_bucket.exports.arn}/arquivos/*"]
  }
}

resource "aws_iam_role_policy" "worker" {
  name   = "${local.name}-worker-access"
  role   = aws_iam_role.worker.id
  policy = data.aws_iam_policy_document.worker.json
}

resource "aws_iam_role" "portal" {
  name               = "${local.name}-portal"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume_role.json
}
