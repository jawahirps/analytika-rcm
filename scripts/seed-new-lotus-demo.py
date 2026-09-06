"""Idempotently seed six months of synthetic New Lotus data into a demo DB."""

import calendar
import datetime as dt
import json
import random
import sqlite3
import sys
import uuid


def main(db_path: str) -> None:
    rng = random.Random(20260906)
    today = dt.date(2026, 9, 6)
    months = [(2026, month) for month in range(4, 10)]
    connection = sqlite3.connect(db_path, timeout=30)
    connection.execute("PRAGMA busy_timeout=30000")
    connection.execute("PRAGMA foreign_keys=ON")
    utc_now = lambda: dt.datetime.now(dt.UTC).isoformat()

    with connection:
        facility = connection.execute(
            "SELECT Id FROM Facilities WHERE Name = ?", ("New Lotus MC",)
        ).fetchone()
        if facility:
            facility_id = facility[0]
        else:
            cursor = connection.execute(
                "INSERT INTO Facilities (Name, IsActive, FullName, LicenseCode) VALUES (?, 1, ?, ?)",
                ("New Lotus MC", "New Lotus Medical Center (Demo)", "DHA-F-DEMO-NL"),
            )
            facility_id = cursor.lastrowid

        existing = connection.execute(
            "SELECT COUNT(*) FROM XmlParsedRecords WHERE FacilityId = ? AND ClaimId LIKE 'DEMO-NL-%'",
            (facility_id,),
        ).fetchone()[0]
        if existing:
            print(json.dumps({"facilityId": facility_id, "skipped": True, "existingRecords": existing}))
            return

        submission_count = remittance_count = activity_count = 0
        for year, month in months:
            volume = 30 if (year, month) == (today.year, today.month) else 150
            last_day = today.day if (year, month) == (today.year, today.month) else calendar.monthrange(year, month)[1]
            for sequence in range(1, volume + 1):
                service_date = dt.date(year, month, 1 + ((sequence * 7) % last_day))
                claim_id = f"DEMO-NL-{year}{month:02d}-{sequence:04d}"
                member_id = f"DEMO-M-{(month * 1000 + sequence) % 420:04d}"
                clinician = f"DHA-P-DEMO-{1 + sequence % 8:02d}"
                payer = ["INS012", "INS038", "INS044"][sequence % 3]
                receiver = ["TPA001", "TPA008", "TPA021"][sequence % 3]
                encounter = "Dental" if sequence % 7 == 0 else "Outpatient"
                diagnosis = ["J06.9", "M54.5", "K02.9", "E11.9"][sequence % 4]
                diagnoses = json.dumps([{"Type": "Principal", "Code": diagnosis}])
                file_id = str(uuid.uuid5(uuid.NAMESPACE_URL, "demo-submit-" + claim_id))
                file_name = f"DEMO-NL-{year}{month:02d}-SUB.xml"
                date_text = service_date.strftime("%d/%m/%Y 09:00")
                sync_period = service_date.strftime("%Y-%m")

                portal_cursor = connection.execute(
                    """INSERT INTO PortalTransactions
                    (Portal, FacilityId, TransactionId, Type, Status, Direction, FileId, FileName,
                     FileDownloaded, FileSizeBytes, FileDownloadedAt, TransactionDate, Payer,
                     Amount, Operation, SyncPeriod, SyncedAt)
                    VALUES ('DHA-DEMO', ?, ?, 'Claim', 'Downloaded', 'Outbound', ?, ?, 1, 1024, ?, ?, ?, '140.00', 'DemoSeed', ?, ?)""",
                    (facility_id, file_id, file_id, file_name, date_text, date_text, payer, sync_period, utc_now()),
                )
                record_cursor = connection.execute(
                    """INSERT INTO XmlParsedRecords
                    (PortalTransactionId, FacilityId, RecordKind, ClaimId, FileName, FileId, TransactionDate,
                     SenderId, ReceiverId, ReceiverName, PayerId, PayerName, PatientId, MemberId,
                     TreatmentDate, TreatmentDateEnd, SubmissionDate, EncounterType, Clinician,
                     ServiceYear, ServiceMonth, GrossAmount, NetAmount, PaidAmount, ActivityCount,
                     ResubmissionType, PrincipalDiagnosis, DiagnosesJson, IsMatched, ReadyForReport, ParsedAt, MatchedAt)
                    VALUES (?, ?, 'Submission', ?, ?, ?, ?, 'DHA-F-DEMO-NL', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
                            '160.00', '140.00', '0.00', 3, '', ?, ?, ?, 1, ?, ?)""",
                    (portal_cursor.lastrowid, facility_id, claim_id, file_name, file_id, date_text,
                     receiver, receiver, payer, payer, f"DEMO-P-{sequence:05d}", member_id,
                     date_text, date_text, date_text, encounter, clinician, str(year), service_date.strftime("%B"),
                     diagnosis, diagnoses, 1 if sequence % 100 < 85 else 0,
                     utc_now(), utc_now()),
                )
                for code, activity_type, net in [("9", "8", "100.00"), ("85025", "3", "25.00"), ("DEMO-RX-01", "5", "15.00")]:
                    connection.execute(
                        """INSERT INTO XmlParsedActivities
                        (XmlParsedRecordId, ActivityCode, ActivityType, Quantity, Net, Gross, PaymentAmount, Clinician, Start, OrderingClinician)
                        VALUES (?, ?, ?, '1', ?, ?, '0.00', ?, ?, '')""",
                        (record_cursor.lastrowid, code, activity_type, net, net, clinician, date_text),
                    )
                    activity_count += 1
                submission_count += 1

                if sequence % 100 >= 85:
                    continue
                denied = sequence % 10 == 0
                paid = "0.00" if denied else "126.00"
                denial_json = '["CODE-016"]' if denied else None
                remit_id = str(uuid.uuid5(uuid.NAMESPACE_URL, "demo-remit-" + claim_id))
                settlement = service_date + dt.timedelta(days=21)
                settlement_text = settlement.strftime("%d/%m/%Y 12:00")
                remit_file = f"DEMO-NL-{year}{month:02d}-RA.xml"
                remit_portal = connection.execute(
                    """INSERT INTO PortalTransactions
                    (Portal, FacilityId, TransactionId, Type, Status, Direction, FileId, FileName,
                     FileDownloaded, FileSizeBytes, FileDownloadedAt, TransactionDate, Payer,
                     Amount, Operation, SyncPeriod, SyncedAt)
                    VALUES ('DHA-DEMO', ?, ?, 'Remittance', 'Downloaded', 'Inbound', ?, ?, 1, 1024, ?, ?, ?, ?, 'DemoSeed', ?, ?)""",
                    (facility_id, remit_id, remit_id, remit_file, settlement_text, settlement_text, payer, paid,
                     settlement.strftime("%Y-%m"), utc_now()),
                )
                remit_record = connection.execute(
                    """INSERT INTO XmlParsedRecords
                    (PortalTransactionId, FacilityId, RecordKind, ClaimId, FileName, FileId, TransactionDate,
                     SenderId, ReceiverId, GrossAmount, NetAmount, PaidAmount, ActivityCount, PaymentReference,
                     SettlementDate, DenialCodesJson, Comments, IdPayer, ClaimCategory, IsMatched,
                     ReadyForReport, ParsedAt, MatchedAt)
                    VALUES (?, ?, 'Remittance', ?, ?, ?, ?, ?, 'DHA-F-DEMO-NL', '160.00', '140.00', ?, 3, ?, ?, ?, ?, ?, ?, 1, 1, ?, ?)""",
                    (remit_portal.lastrowid, facility_id, claim_id, remit_file, remit_id, settlement_text,
                     receiver, paid, f"DEMO-PAY-{year}{month:02d}-{sequence:04d}", settlement_text,
                     denial_json, "Synthetic demo denial" if denied else "Claim approved",
                     f"DEMO-PC-{sequence:05d}", "Technical" if denied else "None",
                     utc_now(), utc_now()),
                )
                payments = ["0.00", "0.00", "0.00"] if denied else ["90.00", "22.50", "13.50"]
                for (code, activity_type, net), payment in zip(
                    [("9", "8", "100.00"), ("85025", "3", "25.00"), ("DEMO-RX-01", "5", "15.00")], payments
                ):
                    connection.execute(
                        """INSERT INTO XmlParsedActivities
                        (XmlParsedRecordId, ActivityCode, ActivityType, Quantity, Net, Gross, PaymentAmount,
                         DenialCode, Clinician, Start, OrderingClinician)
                        VALUES (?, ?, ?, '1', ?, ?, ?, ?, ?, ?, '')""",
                        (remit_record.lastrowid, code, activity_type, net, net, payment,
                         "CODE-016" if denied else "", clinician, date_text),
                    )
                    activity_count += 1
                remittance_count += 1

    connection.close()
    print(json.dumps({"facilityId": facility_id, "submissions": submission_count,
                      "remittances": remittance_count, "activities": activity_count,
                      "from": "2026-04-01", "to": str(today)}))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: seed-new-lotus-demo.py <demo-db-path>")
    main(sys.argv[1])
