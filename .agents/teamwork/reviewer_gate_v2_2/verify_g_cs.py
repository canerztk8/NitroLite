import re

with open(r'NitroLite\obj\Debug\net10.0-windows\MainWindow.g.cs', 'r', encoding='utf-8') as f:
    g_content = f.read()

fields = re.findall(r'internal\s+[\w\.<>]+\s+(\w+);', g_content)
print(f"Generated fields in MainWindow.g.cs: {len(fields)}")

with open(r'.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md', 'r', encoding='utf-8') as f:
    survey = f.read()

survey_names = set(re.findall(r'\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|RadioButton|Grid|ProgressBar|CheckBox|ComboBox|Button|Canvas|Polygon|Polyline|TextBox|Slider)`?', survey))
survey_names.discard('ValueChanged')
print(f"Survey unique required names: {len(survey_names)}")

missing = [n for n in survey_names if n not in fields]
print(f"Missing from generated MainWindow.g.cs: {missing}")

with open(r'NitroLite\obj\Debug\net10.0-windows\OsdOverlayWindow.g.cs', 'r', encoding='utf-8') as f:
    osd_g_content = f.read()

osd_fields = re.findall(r'internal\s+[\w\.<>]+\s+(\w+);', osd_g_content)
print(f"Generated fields in OsdOverlayWindow.g.cs: {len(osd_fields)}")
osd_required = ['TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage', 'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram', 'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan']
missing_osd = [n for n in osd_required if n not in osd_fields]
print(f"Missing from generated OsdOverlayWindow.g.cs: {missing_osd}")
