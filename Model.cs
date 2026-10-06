using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XadoSpec
{
    sealed class Line
    {
        public string Key, Value;
        public bool Header, Weak;
    }

    sealed class Section
    {
        public readonly string Title, Glyph, Color;
        public readonly List<Line> Lines = new List<Line>();

        public Section(string title, string glyph, string color)
        {
            Title = title;
            Glyph = glyph;
            Color = color;
        }

        public void Add(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) Note(key, "не удалось определить");
            else Lines.Add(new Line { Key = key, Value = value.Trim() });
        }

        public void AddReal(string key, string value)
        {
            var v = Collector.Real(value);
            if (v == null) Note(key, "не указано производителем");
            else Lines.Add(new Line { Key = key, Value = v });
        }

        public void Note(string key, string value) => Lines.Add(new Line { Key = key, Value = value, Weak = true });

        public void Head(string text) => Lines.Add(new Line { Value = text, Header = true });
    }

    sealed class Tile
    {
        public string Caption, Value, Sub, Color;
    }

    sealed class Report
    {
        public readonly DateTime Time = DateTime.Now;
        public TimeSpan Took;
        public string Title, Subtitle;
        public Tile Cpu, Ram, Gpu, Disk;
        public readonly List<Section> Sections = new List<Section>();

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("XadoSpec — характеристики ПК");
            sb.AppendLine("Снято: " + Time.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            foreach (var s in Sections)
            {
                sb.AppendLine();
                sb.AppendLine("■ " + s.Title.ToUpperInvariant());
                foreach (var l in s.Lines)
                {
                    if (l.Header) sb.AppendLine("  ▸ " + l.Value);
                    else sb.AppendLine("    " + l.Key + ": " + l.Value);
                }
            }
            return sb.ToString();
        }
    }
}
