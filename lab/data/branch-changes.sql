-- Lab step 6: change data on your feature branch only. Run after `git checkout -b`:
--   dotnet run --project src/Lakebase.Cli -- lakebase db seed lab/data/branch-changes.sql
-- Jane Doe (M-1001) gets 3 new care events and one edited description. production keeps
-- her original 2 events: the branch is a copy-on-write copy, so writes never flow back.
-- Re-running changes nothing.

INSERT INTO "CareEvents" ("MemberId", "EventType", "Description", "EventDate")
SELECT m."Id", e.event_type, e.description, e.event_date::timestamptz
FROM (VALUES
    ('Telehealth', 'Branch-only: follow-up call',    '2026-10-01T15:00:00Z'),
    ('Imaging',    'Branch-only: chest X-ray',       '2026-10-05T11:30:00Z'),
    ('Pharmacy',   'Branch-only: new prescription',  '2026-10-06T09:45:00Z')
) AS e(event_type, description, event_date)
JOIN "Members" m ON m."MemberId" = 'M-1001'
WHERE NOT EXISTS (
    SELECT 1 FROM "CareEvents" c
    WHERE c."MemberId" = m."Id" AND c."Description" = e.description
);

UPDATE "CareEvents"
SET "Description" = 'Annual wellness exam (edited on branch)'
WHERE "Description" = 'Annual wellness exam'
  AND "MemberId" = (SELECT "Id" FROM "Members" WHERE "MemberId" = 'M-1001');
