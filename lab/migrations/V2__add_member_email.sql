-- Lab step 1: Members.Email. Fixes /api/members (the Member model already maps Email).

ALTER TABLE "Members"
    ADD COLUMN IF NOT EXISTS "Email" text;
