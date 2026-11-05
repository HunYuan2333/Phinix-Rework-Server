"""Synthetic configuration only: trace switch and existing quota edits preserve each other."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('trade_config', Path(__file__).parents[1] / 'configure_trade_limits.py')
config = importlib.util.module_from_spec(spec)
spec.loader.exec_module(config)

class TradeDiagnosticConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'server.conf'
        outer = ET.Element('Config')
        entries = ET.SubElement(outer, 'ExtensionConfigs')
        entry = ET.SubElement(entries, 'KeyValueOfstringstring')
        ET.SubElement(entry, 'Key').text = 'builtin.trade'
        self.value = ET.SubElement(entry, 'Value')
        self.section = ET.Element('{' + config.SECTION_NS + '}TradeServerConfig')
        ET.SubElement(self.section, '{' + config.SECTION_NS + '}DatabasePath').text = 'synthetic-trades'
        self.outer = outer

    def save(self, value=None):
        if value is not None:
            ET.SubElement(self.section, '{' + config.SECTION_NS + '}DiagnosticLogging').text = value
        self.value.text = ET.tostring(self.section, encoding='unicode')
        ET.ElementTree(self.outer).write(self.source)

    def test_old_defaults_off(self):
        self.save()
        result = config.create_candidate(self.source, self.root/'next', {'RecoveryPageSize': 3})
        self.assertIs(result['DiagnosticLogging'], False)
        self.assertEqual(result['RecoveryPageSize'], 3)

    def test_enable_and_disable(self):
        self.save()
        first = self.root/'enabled'
        self.assertTrue(config.create_candidate(self.source, first, {}, True)['DiagnosticLogging'])
        self.assertFalse(config.create_candidate(first, self.root/'disabled', {}, False)['DiagnosticLogging'])

    def test_quota_edit_preserves_enabled(self):
        self.save('true')
        target = self.root/'next'
        self.assertTrue(config.create_candidate(self.source, target, {'PrincipalRetainedTradeLimit': 0})['DiagnosticLogging'])
        inner = ET.fromstring(next(e for e in ET.parse(target).getroot().iter() if config.local(e)=='Value').text)
        self.assertEqual([config.local(e) for e in inner][-2:], ['StorageMiBLimit','DiagnosticLogging'])
        self.assertEqual(next(e.text for e in inner if config.local(e)=='DatabasePath'), 'synthetic-trades')

    def test_invalid_existing_switch_preserves_input(self):
        self.save('bad')
        before = self.source.read_bytes()
        with self.assertRaises(ValueError):
            config.create_candidate(self.source, self.root/'next', {}, True)
        self.assertEqual(before, self.source.read_bytes())
        self.assertFalse((self.root/'next').exists())

    def test_output_cannot_overwrite(self):
        self.save()
        target=self.root/'next'; target.write_text('retained')
        with self.assertRaises(FileExistsError):
            config.create_candidate(self.source, target, {}, True)
        self.assertEqual(target.read_text(), 'retained')

    def test_original_trading_toggle_preserves_unrelated_configuration(self):
        self.save('true')
        source_bytes = self.source.read_bytes()
        enabled, disabled = self.root/'on', self.root/'off'
        config.create_candidate(self.source, enabled, {}, original_trading=True)
        config.create_candidate(enabled, disabled, {}, original_trading=False)
        def section(path):
            entries = next(e for e in ET.parse(path).getroot() if config.local(e)=='ExtensionConfigs')
            row = next(row for row in entries if any(config.local(c)=='Key' and c.text=='builtin.original-trade-adapter' for c in row))
            return ET.fromstring(next(c.text for c in row if config.local(c)=='Value'))
        self.assertEqual(next(e.text for e in section(enabled) if config.local(e)=='Enabled'), 'true')
        self.assertEqual(next(e.text for e in section(disabled) if config.local(e)=='Enabled'), 'false')
        self.assertEqual(source_bytes, self.source.read_bytes())

    def test_original_trading_rejects_corrupt_existing_section(self):
        self.save()
        entry = ET.SubElement(next(e for e in self.outer if config.local(e)=='ExtensionConfigs'), 'KeyValueOfstringstring')
        ET.SubElement(entry, 'Key').text = 'builtin.original-trade-adapter'
        ET.SubElement(entry, 'Value').text = '<wrong />'
        ET.ElementTree(self.outer).write(self.source)
        with self.assertRaises(ValueError):
            config.create_candidate(self.source, self.root/'next', {}, original_trading=True)
        self.assertFalse((self.root/'next').exists())

    def test_original_chat_toggle_preserves_trade_switch(self):
        self.save()
        both, chat_off = self.root/'both', self.root/'chat-off'
        config.create_candidate(self.source, both, {}, original_trading=True, original_chat=True)
        config.create_candidate(both, chat_off, {}, original_chat=False)
        outer = ET.parse(chat_off).getroot()
        values = {next(c.text for c in row if config.local(c)=='Key'): next(c.text for c in row if config.local(c)=='Value') for row in next(e for e in outer if config.local(e)=='ExtensionConfigs')}
        self.assertEqual(next(e.text for e in ET.fromstring(values['builtin.original-trade-adapter']) if config.local(e)=='Enabled'), 'true')
        self.assertEqual(next(e.text for e in ET.fromstring(values['builtin.legacy-chat-adapter']) if config.local(e)=='Enabled'), 'false')

    def test_original_chat_rejects_wrong_section(self):
        self.save()
        entry = ET.SubElement(next(e for e in self.outer if config.local(e)=='ExtensionConfigs'), 'KeyValueOfstringstring')
        ET.SubElement(entry, 'Key').text = 'builtin.legacy-chat-adapter'
        ET.SubElement(entry, 'Value').text = '<wrong />'
        ET.ElementTree(self.outer).write(self.source)
        with self.assertRaises(ValueError):
            config.create_candidate(self.source, self.root/'next', {}, original_chat=True)
        self.assertFalse((self.root/'next').exists())
