-- Lab step 2: Providers.Email and rename CommentsText → Notes. Fixes /api/providers.

ALTER TABLE "Providers"
    ADD COLUMN IF NOT EXISTS "Email" text;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'Providers'
          AND column_name = 'CommentsText'
    ) AND NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'Providers'
          AND column_name = 'Notes'
    ) THEN
        ALTER TABLE "Providers" RENAME COLUMN "CommentsText" TO "Notes";
    END IF;
END $$;
