using System.Text;

namespace Zapretiks.Infrastructure;

internal sealed class SteamKeyValues
{
    public sealed record Value(string? Text, SteamKeyValues? Object)
    {
        public string? GetObjectValue(string name) => Object?.GetText(name);
    }

    private readonly Dictionary<string, Value> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, Value>> Values => _values;
    public string? GetText(string name) => _values.GetValueOrDefault(name)?.Text;
    public SteamKeyValues? GetObject(string name) => _values.GetValueOrDefault(name)?.Object;

    public static SteamKeyValues Parse(string data) => new Parser(data).ReadObject(0);

    private sealed class Parser(string data)
    {
        private int _position;

        public SteamKeyValues ReadObject(int depth)
        {
            if (depth > 12)
                throw InvalidFormat();

            var result = new SteamKeyValues();
            while (true)
            {
                SkipSpacing();
                if (_position == data.Length && depth == 0)
                    return result;

                if (depth > 0 && Consume('}'))
                    return result;

                var key = ReadToken();
                SkipSpacing();
                var value = Consume('{') ? new Value(null, ReadObject(depth + 1)) : new Value(ReadToken(), null);
                result._values.TryAdd(key, value);
            }
        }

        private string ReadToken()
        {
            if (!Consume('"'))
            {
                var start = _position;
                while (_position < data.Length && !char.IsWhiteSpace(data[_position]) && data[_position] is not '{' and not '}')
                    _position++;

                return _position > start ? data[start.._position] : throw InvalidFormat();
            }

            var text = new StringBuilder();
            while (_position < data.Length)
            {
                var ch = data[_position++];
                if (ch == '"')
                    return text.ToString();

                if (ch == '\\' && _position < data.Length && data[_position] is '\\' or '"')
                    ch = data[_position++];

                text.Append(ch);
            }

            throw InvalidFormat();
        }

        private bool Consume(char ch)
        {
            if (_position >= data.Length || data[_position] != ch)
                return false;

            _position++;
            return true;
        }

        private void SkipSpacing()
        {
            while (_position < data.Length)
            {
                if (char.IsWhiteSpace(data[_position]))
                    _position++;
                else if (data.AsSpan(_position).StartsWith("//", StringComparison.Ordinal))
                {
                    while (_position < data.Length && data[_position] != '\n')
                        _position++;
                }
                else
                    return;
            }
        }

        private static InvalidDataException InvalidFormat() => new("Некорректный формат файла Steam.");
    }
}
