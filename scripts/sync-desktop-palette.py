from pathlib import Path
import xml.etree.ElementTree as E
root=Path.cwd(); out=root/'src/Steamy.Desktop/Styles'; out.mkdir(exist_ok=True)
E.register_namespace('','https://github.com/avaloniaui'); E.register_namespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
a='https://github.com/avaloniaui'; x='http://schemas.microsoft.com/winfx/2006/xaml'
doc=E.Element('{'+a+'}ResourceDictionary'); themes=E.SubElement(doc,'{'+a+'}ResourceDictionary.ThemeDictionaries')
allowed={'Color','SolidColorBrush','LinearGradientBrush','RadialGradientBrush','Thickness','CornerRadius','Double'}
for theme in ['Dark','Light']:
    source=E.parse(root/f'src/Steamy/Resources/Themes/{theme}.xaml').getroot()
    dest=E.SubElement(themes,'{'+a+'}ResourceDictionary',{'{'+x+'}Key':theme})
    for child in source:
        name=child.tag.split('}')[-1]
        if name not in allowed: continue
        for node in child.iter():
            tag=node.tag.split('}')[-1]
            node.tag='{'+(x if tag=='Double' else a)+'}'+tag
            for attr in ['StartPoint','EndPoint','Center','GradientOrigin']:
                if attr in node.attrib:
                    node.set(attr,','.join(f'{float(v)*100:g}%' for v in node.get(attr).split(',')))
            for attr in ['RadiusX','RadiusY']:
                if attr in node.attrib: node.set(attr,f'{float(node.get(attr))*100:g}%')
        dest.append(child)
E.indent(doc)
(out/'WindowsPalette.axaml').write_text('<!-- Generated from the existing WPF Dark/Light palettes; run scripts/sync-desktop-palette.py. -->\n'+E.tostring(doc,encoding='unicode'),encoding='utf-8')
