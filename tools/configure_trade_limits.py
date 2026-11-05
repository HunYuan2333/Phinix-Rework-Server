#!/usr/bin/env python3
"""Create a reviewed server.conf candidate; never overwrite the input or an existing output."""
import argparse
import os
from pathlib import Path
import xml.etree.ElementTree as ET

DEFAULTS = {"PrincipalRetainedTradeLimit": 32, "PrincipalPayloadByteLimit": 1048576,
            "PrincipalAuxiliaryByteLimit": 65536, "RecoveryPageSize": 8, "StorageMiBLimit": 256}
SECTION_NS = "http://schemas.datacontract.org/2004/07/Phinix.TradeExtension.Server"
ARRAY_NS = "http://schemas.microsoft.com/2003/10/Serialization/Arrays"


def local(element):
    return element.tag.rsplit("}", 1)[-1]


def create_candidate(source, output, updates, diagnostic_logging=None, original_trading=None, original_chat=None):
    source, output = Path(source), Path(output)
    if source.is_symlink() or source.resolve() == output.resolve():
        raise ValueError("Use a regular input and a distinct new output")
    tree = ET.parse(source)
    root = tree.getroot()
    configs = next((e for e in root if local(e) == "ExtensionConfigs"), None)
    if configs is None:
        raise ValueError("Missing ExtensionConfigs; first generate a current server.conf")
    configs.attrib.pop("{http://www.w3.org/2001/XMLSchema-instance}nil", None)
    entries = [e for e in configs if any(local(c) == "Key" and c.text == "builtin.trade" for c in e)]
    if len(entries) > 1:
        raise ValueError("Duplicate builtin.trade configuration")
    if entries:
        value = next(c for c in entries[0] if local(c) == "Value")
        section = ET.fromstring(value.text or "")
        if section.tag != "{" + SECTION_NS + "}TradeServerConfig":
            raise ValueError("Unexpected trade section type")
    else:
        entry = ET.SubElement(configs, "{" + ARRAY_NS + "}KeyValueOfstringstring")
        ET.SubElement(entry, "{" + ARRAY_NS + "}Key").text = "builtin.trade"
        value = ET.SubElement(entry, "{" + ARRAY_NS + "}Value")
        section = ET.Element("{" + SECTION_NS + "}TradeServerConfig")
        ET.SubElement(section, "{" + SECTION_NS + "}DatabasePath").text = "trades"
    # DataContract order is significant. Preserve unknown elements after known ones.
    effective = dict(DEFAULTS)
    for element in list(section):
        name = local(element)
        if name in DEFAULTS:
            try:
                effective[name] = int(element.text)
            except (TypeError, ValueError):
                raise ValueError("Invalid existing numeric trade limit") from None
            section.remove(element)
    diagnostics = [e for e in section if local(e) == "DiagnosticLogging"]
    if len(diagnostics) > 1:
        raise ValueError("Duplicate trade DiagnosticLogging")
    tracing = False
    if diagnostics:
        text = (diagnostics[0].text or "").strip()
        if text not in ("true", "false", "1", "0"):
            raise ValueError("Invalid trade DiagnosticLogging boolean")
        tracing = text in ("true", "1")
        section.remove(diagnostics[0])
    if diagnostic_logging is not None:
        if not isinstance(diagnostic_logging, bool):
            raise ValueError("DiagnosticLogging must be a boolean")
        tracing = diagnostic_logging
    effective.update(updates)
    if any(v < 0 or v > 2147483647 for v in effective.values()) or not 1 <= effective["RecoveryPageSize"] <= 64:
        raise ValueError("Invalid limits: quota 0 disables it; page size must be 1..64")
    if effective["StorageMiBLimit"] != 0 and not 32 <= effective["StorageMiBLimit"] <= 16384:
        raise ValueError("Storage limit must be 0 or 32..16384 MiB")
    database = next((e for e in section if local(e) == "DatabasePath"), None)
    if database is None:
        database = ET.Element("{" + SECTION_NS + "}DatabasePath")
        database.text = "trades"
        section.insert(0, database)
    offset = list(section).index(database) + 1
    for index, (name, number) in enumerate(effective.items()):
        element = ET.Element("{" + SECTION_NS + "}" + name); element.text = str(number)
        section.insert(offset + index, element)
    diagnostic = ET.Element("{" + SECTION_NS + "}DiagnosticLogging")
    diagnostic.text = "true" if tracing else "false"
    section.insert(offset + len(effective), diagnostic)
    value.text = ET.tostring(section, encoding="unicode")
    adapter_ns = "http://schemas.datacontract.org/2004/07/Phinix.LegacyAdapter.Server"
    for key, section_type, enabled in [
        ("builtin.original-trade-adapter", "OriginalTradeServerConfig", original_trading),
        ("builtin.legacy-chat-adapter", "LegacyChatServerConfig", original_chat),
    ]:
        if enabled is None:
            continue
        if not isinstance(enabled, bool):
            raise ValueError("Adapter Enabled must be a boolean")
        rows = [e for e in configs if any(local(c) == "Key" and c.text == key for c in e)]
        if len(rows) > 1:
            raise ValueError("Duplicate adapter configuration")
        if rows:
            adapter_value = next(c for c in rows[0] if local(c) == "Value")
            adapter = ET.fromstring(adapter_value.text or "")
            if adapter.tag != "{" + adapter_ns + "}" + section_type:
                raise ValueError("Unexpected adapter section type")
            flags = [e for e in adapter if local(e) == "Enabled"]
            if len(flags) > 1 or any((e.text or "").strip() not in ("true", "false", "1", "0") for e in flags):
                raise ValueError("Invalid adapter Enabled")
            for flag in flags:
                adapter.remove(flag)
        else:
            row = ET.SubElement(configs, "{" + ARRAY_NS + "}KeyValueOfstringstring")
            ET.SubElement(row, "{" + ARRAY_NS + "}Key").text = key
            adapter_value = ET.SubElement(row, "{" + ARRAY_NS + "}Value")
            adapter = ET.Element("{" + adapter_ns + "}" + section_type)
        flag = ET.Element("{" + adapter_ns + "}Enabled")
        flag.text = "true" if enabled else "false"
        adapter.insert(0, flag)
        adapter_value.text = ET.tostring(adapter, encoding="unicode")
    # Never print/copy whole configurations as evidence; output may hold private options.
    descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            os.fchmod(handle.fileno(), source.stat().st_mode & 0o777)
            tree.write(handle, encoding="utf-8", xml_declaration=True)
            handle.flush(); os.fsync(handle.fileno())
    except BaseException:
        output.unlink(missing_ok=True)
        raise
    return {**effective, "DiagnosticLogging": tracing}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    for option in ["retained-trades", "payload-bytes", "auxiliary-bytes", "page-size", "storage-mib"]:
        parser.add_argument("--" + option, type=int)
    parser.add_argument("--diagnostic-logging", choices=["on", "off"])
    parser.add_argument("--original-trading", choices=["on", "off"], help="Opt in to the lossy original Phinix trade profile")
    parser.add_argument("--original-chat", choices=["on", "off"], help="Enable original plain-text chat and explicit reply/mention text projection")
    args = parser.parse_args()
    updates = {key: value for key, value in zip(DEFAULTS, [args.retained_trades, args.payload_bytes, args.auxiliary_bytes, args.page_size, args.storage_mib]) if value is not None}
    if not updates and args.diagnostic_logging is None and args.original_trading is None and args.original_chat is None:
        parser.error("Specify a limit, --diagnostic-logging, --original-trading or --original-chat")
    effective = create_candidate(args.input, args.output, updates, None if args.diagnostic_logging is None else args.diagnostic_logging == "on", None if args.original_trading is None else args.original_trading == "on", None if args.original_chat is None else args.original_chat == "on")
    if args.original_trading is not None:
        print("OriginalTrading=" + args.original_trading + "; basic item attributes only; original confirmation/delivery guarantees.")
    if args.original_chat is not None:
        print("OriginalChat=" + args.original_chat + "; replies and mentions are rendered as explicit text.")
    print("Candidate created; replace the original only while the server is stopped. Restart required.")
    print(" ".join(f"{key}={value}" for key, value in effective.items()))


if __name__ == "__main__":
    main()
