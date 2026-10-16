-- Reconstructs V2 already applied on production (2026-10-04, description "add member email").
-- Checksum will not match until this feature branch is repaired; do not repair production.

ALTER TABLE "Members"
    ADD COLUMN IF NOT EXISTS "Email" text;
