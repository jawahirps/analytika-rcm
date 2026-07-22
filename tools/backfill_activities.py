#!/usr/bin/env python3
"""
Backfill XmlParsedActivities from the stored claim XML.

The parser extracts <Activity> rows but only ~8k of 780k claims currently have
them persisted. This re-reads each PortalTransaction's FileContentXml ONCE,
extracts every claim's activities, and inserts them keyed to the matching
XmlParsedRecord (by PortalTransactionId + ClaimId). Mirrors the C# extraction
in XmlParsingService (Code, Type, Quantity, Net, Gross, PaymentAmount,
DenialCode, Clinician, Start, OrderingClinician).

Scope with --facility <id> (recommended first); omit for all facilities.
Idempotent: clears a record's existing activities before inserting.

Usage:
  python tools/backfill_activities.py [--facility N] [--limit-files N] [--db PATH]
"""
import argparse, sqlite3, sys, time, re
import xml.etree.ElementTree as ET

DB_DEFAULT = r"J:\GhafAnalytika\Analytika\analytika.db"

def local(tag):  # strip namespace
    return tag.rsplit('}', 1)[-1]

def child_text(el, name):
    for c in el:
        if local(c.tag) == name:
            return (c.text or "").strip()
    return None

def iter_claim_activities(xml_text):
    """Yield (claim_id, [activity dicts]) for each Claim in the document."""
    if not xml_text or len(xml_text) < 20:
        return
    try:
        root = ET.fromstring(xml_text)
    except ET.ParseError:
        return
    for claim in root.iter():
        if local(claim.tag) != "Claim":
            continue
        cid = child_text(claim, "ID")
        if not cid:
            continue
        acts = []
        for act in claim.iter():
            if local(act.tag) != "Activity":
                continue
            def num(n):
                v = child_text(act, n)
                try: return float(v) if v not in (None, "") else 0.0
                except ValueError: return 0.0
            acts.append(dict(
                code=child_text(act, "Code") or "",
                type=child_text(act, "Type") or "",
                qty=num("Quantity"), net=num("Net"), gross=num("Gross"),
                payment=num("PaymentAmount"),
                denial=child_text(act, "DenialCode") or "",
                clin=child_text(act, "Clinician") or "",
                start=child_text(act, "Start") or "",
                ord_clin=child_text(act, "OrderingClinician") or "",
            ))
        if acts:
            yield cid.strip(), acts

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--facility", type=int)
    ap.add_argument("--limit-files", type=int, default=0)
    ap.add_argument("--db", default=DB_DEFAULT)
    args = ap.parse_args()

    ro = sqlite3.connect(f"file:{args.db.replace(chr(92),'/')}?mode=ro", uri=True, timeout=60)
    ro.execute("PRAGMA busy_timeout=45000")
    # (PortalTransactionId, ClaimId) -> [XmlParsedRecordId,...]
    rec_sql = "SELECT Id, PortalTransactionId, ClaimId FROM XmlParsedRecords WHERE PortalTransactionId IS NOT NULL"
    params = []
    if args.facility:
        rec_sql += " AND FacilityId=?"; params.append(args.facility)
    rec_map = {}
    for rid, ptid, cid in ro.execute(rec_sql, params):
        rec_map.setdefault((ptid, (cid or "").strip().lower()), []).append(rid)
    print(f"records in scope: {sum(len(v) for v in rec_map.values()):,} across {len(set(k[0] for k in rec_map)):,} files", flush=True)

    tx_sql = "SELECT Id, FileContentXml FROM PortalTransactions WHERE FileDownloaded=1 AND FileContentXml IS NOT NULL"
    if args.facility:
        tx_sql += f" AND FacilityId={int(args.facility)}"
    if args.limit_files:
        tx_sql += f" LIMIT {int(args.limit_files)}"

    rows_to_insert = []
    touched_record_ids = set()
    t0 = time.time(); files = 0
    for ptid, xml in ro.execute(tx_sql):
        files += 1
        for cid, acts in iter_claim_activities(xml):
            for rid in rec_map.get((ptid, cid.lower()), []):
                touched_record_ids.add(rid)
                for a in acts:
                    rows_to_insert.append((rid, a["code"], a["type"], a["qty"], a["net"],
                                           a["gross"], a["payment"], a["denial"], a["clin"],
                                           a["start"], a["ord_clin"]))
        if files % 200 == 0:
            print(f"  parsed {files:,} files, {len(rows_to_insert):,} activities so far [{time.time()-t0:.0f}s]", flush=True)
    ro.close()
    print(f"parsed {files:,} files -> {len(rows_to_insert):,} activities for {len(touched_record_ids):,} records [{time.time()-t0:.0f}s]", flush=True)

    if not rows_to_insert:
        print("nothing to insert."); return

    for attempt in range(1, 15):
        try:
            w = sqlite3.connect(args.db, timeout=90); w.execute("PRAGMA busy_timeout=90000")
            ids = list(touched_record_ids)
            for i in range(0, len(ids), 900):
                chunk = ids[i:i+900]
                w.execute(f"DELETE FROM XmlParsedActivities WHERE XmlParsedRecordId IN ({','.join('?'*len(chunk))})", chunk)
            w.executemany(
                "INSERT INTO XmlParsedActivities (XmlParsedRecordId,ActivityCode,ActivityType,Quantity,Net,Gross,PaymentAmount,DenialCode,Clinician,Start,OrderingClinician) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?)", rows_to_insert)
            w.commit()
            print(f"committed {len(rows_to_insert):,} activities.")
            print("  total activities now:", w.execute("SELECT COUNT(*) FROM XmlParsedActivities").fetchone()[0])
            w.close(); break
        except sqlite3.OperationalError as e:
            try: w.close()
            except: pass
            print(f"  write retry {attempt}: {e}"); time.sleep(3)

if __name__ == "__main__":
    main()
