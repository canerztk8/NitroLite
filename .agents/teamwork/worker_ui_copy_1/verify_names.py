import re

with open(r'.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md', encoding='utf-8') as f:
    report = f.read()

with open(r'NitroLite\MainWindow.xaml', encoding='utf-8') as f:
    xaml = f.read()

with open(r'NitroLite\OsdOverlayWindow.xaml', encoding='utf-8') as f:
    osd_xaml = f.read()

# Match table entries with control type
pattern = r'\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|RadioButton|Grid|ProgressBar|CheckBox|ComboBox|Button|Canvas|Polygon|Polyline|TextBox|Slider)`?'
names = sorted(list(set(re.findall(pattern, report))))

missing_mw = [n for n in names if f'x:Name="{n}"' not in xaml]
print(f'Total x:Name controls in MainWindow survey: {len(names)}')
print(f'Missing in MainWindow.xaml: {missing_mw}')

osd_pattern = r'\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|StackPanel|Grid)`?.*Osd'
osd_names = ['TxtOsdCpu', 'TxtOsdCpuPower', 'TxtOsdGpu', 'TxtOsdGpuPower', 'TxtOsdRam', 'TxtOsdVram', 'TxtOsdSysTemp', 'TxtOsdFan', 'TxtOsdMode', 'TxtOsdBoost', 'TxtOsdBattery']
missing_osd = [n for n in osd_names if f'x:Name="{n}"' not in osd_xaml]
print(f'Total x:Name in OsdOverlayWindow: {len(osd_names)}')
print(f'Missing in OsdOverlayWindow.xaml: {missing_osd}')
