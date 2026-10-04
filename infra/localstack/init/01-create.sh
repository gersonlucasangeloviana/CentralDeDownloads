#!/bin/sh
set -eu
awslocal s3 mb s3://central-downloads-local
awslocal sqs create-queue --queue-name central-downloads-local
