-- Lab sample data for the V1 schema (~10k rows): 4,005 members, 1,003 providers, 5,004 care
-- events. Run on main (the production branch) before you branch:
--   dotnet run --project src/Lakebase.Cli -- lakebase db seed
-- Idempotent: re-running skips rows that already exist. Not a Flyway migration, so the
-- repo's CI promotion never copies it into dev/staging/production.

INSERT INTO "Members" ("MemberId", "FirstName", "LastName", "DateOfBirth", "PlanType") VALUES
    ('M-1001', 'Jane',   'Doe',      '1990-05-15T00:00:00Z', 'Commercial'),
    ('M-1002', 'John',   'Smith',    '1985-03-20T00:00:00Z', 'Medicare'),
    ('M-1003', 'Priya',  'Patel',    '1978-11-02T00:00:00Z', 'Medicaid'),
    ('M-1004', 'Carlos', 'Gomez',    '2001-07-30T00:00:00Z', 'Commercial'),
    ('M-1005', 'Mei',    'Chen',     '1963-01-12T00:00:00Z', 'Medicare')
ON CONFLICT ("MemberId") DO NOTHING;

-- CommentsText becomes Notes in lab/migrations/V3; these comments should survive the rename.
INSERT INTO "Providers" ("Npi", "Name", "Specialty", "IsActive", "PhoneNumber", "CommentsText") VALUES
    ('1234567890', 'Dr. Ada Lovelace',   'Cardiology',       true,  '555-0100', 'Accepting new patients'),
    ('2345678901', 'Dr. Alan Turing',    'Internal Medicine', true,  '555-0101', 'Telehealth on Fridays'),
    ('3456789012', 'Dr. Grace Hopper',   'Pediatrics',       false, NULL,       'On sabbatical until spring')
ON CONFLICT ("Npi") DO NOTHING;

INSERT INTO "CareEvents" ("MemberId", "EventType", "Description", "EventDate")
SELECT m."Id", e.event_type, e.description, e.event_date::timestamptz
FROM (VALUES
    ('M-1001', 'Office Visit', 'Annual wellness exam',    '2026-09-01T14:30:00Z'),
    ('M-1001', 'Lab Work',     'Routine blood panel',     '2026-09-10T09:00:00Z'),
    ('M-1002', 'Office Visit', 'Cardiology follow-up',    '2026-08-22T10:15:00Z'),
    ('M-1005', 'Pharmacy',     'Statin prescription fill', '2026-09-15T16:45:00Z')
) AS e(member_id, event_type, description, event_date)
JOIN "Members" m ON m."MemberId" = e.member_id
WHERE NOT EXISTS (
    SELECT 1 FROM "CareEvents" c
    WHERE c."MemberId" = m."Id" AND c."EventType" = e.event_type AND c."EventDate" = e.event_date::timestamptz
);


-- Generated bulk rows. Deterministic (no random()), so re-runs find the same keys and skip them.
INSERT INTO "Members" ("MemberId", "FirstName", "LastName", "DateOfBirth", "PlanType")
SELECT
    'M-' || lpad(i::text, 6, '0'),
    (ARRAY['Ava','Ben','Chloe','Dev','Elena','Felix','Grace','Hiro','Isla','Jamal','Kira','Liam','Maya','Noah','Omar','Pia'])[i % 16 + 1],
    (ARRAY['Anderson','Brown','Clark','Davis','Evans','Garcia','Hughes','Ito','Johnson','Khan','Lee','Martinez','Nguyen','Okafor','Rossi','Singh','Taylor','Walker'])[i % 18 + 1],
    timestamptz '1940-01-01 00:00:00Z' + make_interval(days => (i * 37) % 30000),
    (ARRAY['Commercial','Medicare','Medicaid','Marketplace'])[i % 4 + 1]
FROM generate_series(1, 4000) AS i
ON CONFLICT ("MemberId") DO NOTHING;

INSERT INTO "Providers" ("Npi", "Name", "Specialty", "IsActive", "PhoneNumber", "CommentsText")
SELECT
    (9000000000 + i)::text,
    'Dr. ' || (ARRAY['Rivera','Okoro','Lindqvist','Haddad','Moreau','Tanaka','Kowalski','Mensah','Silva','Novak'])[i % 10 + 1] || ' ' || i,
    (ARRAY['Cardiology','Dermatology','Family Medicine','Internal Medicine','Neurology','Oncology','Orthopedics','Pediatrics','Psychiatry','Radiology'])[i % 10 + 1],
    i % 9 <> 0,
    '555-' || lpad((i % 10000)::text, 4, '0'),
    CASE WHEN i % 3 = 0 THEN NULL
         ELSE (ARRAY['Accepting new patients','Telehealth available','Spanish spoken','Evening hours','Referral required'])[i % 5 + 1]
    END
FROM generate_series(1, 1000) AS i
ON CONFLICT ("Npi") DO NOTHING;

INSERT INTO "CareEvents" ("MemberId", "EventType", "Description", "EventDate")
SELECT m."Id", g.event_type, g.description, g.event_date
FROM (
    SELECT
        'M-' || lpad(((i - 1) % 4000 + 1)::text, 6, '0') AS member_id,
        (ARRAY['Office Visit','Lab Work','Pharmacy','Telehealth','Imaging','Emergency'])[i % 6 + 1] AS event_type,
        (ARRAY['Annual wellness exam','Routine blood panel','Prescription fill','Follow-up consult','X-ray','Urgent visit'])[i % 6 + 1] AS description,
        timestamptz '2025-01-01 08:00:00Z' + make_interval(hours => (i * 7) % 15000) AS event_date
    FROM generate_series(1, 5000) AS i
) AS g
JOIN "Members" m ON m."MemberId" = g.member_id
WHERE NOT EXISTS (
    SELECT 1 FROM "CareEvents" c
    WHERE c."MemberId" = m."Id" AND c."EventType" = g.event_type AND c."EventDate" = g.event_date
);
