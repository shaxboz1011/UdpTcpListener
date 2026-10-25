# Custom protocols

UdpTcpListener can decode your own binary protocols, such as a sensor's UDP packets or a game's
messages, from a small JSON file. No programming is needed.

Decoded packets show:
- the protocol's summary in the **Info** column,
- every field at the top of the **Packet details** pane,
- the same output in the CLI with `-d/--decode`.

## Where the files go

| | Folder |
|---|---|
| Windows | `%APPDATA%\UdpTcpListener\protocols\` |
| Linux | `~/.config/UdpTcpListener/protocols/` |

On first run the app creates this folder with a commented `example-sensor.json`. In the app,
open **Packet details → Protocols ▾**, then use **Open folder**, and **Reload** after editing.
Files with errors are listed in that menu with the reason. The CLI reads the same folder, or the
one you pass with `--protocols <dir>`.

## Example

```jsonc
{
  "name": "Example sensor",
  "transport": "udp",          // udp | tcp | any
  "ports": [9000],             // matches if either side uses one of these ports
  "endian": "big",             // default byte order: big (network order) or little
  "summary": "device {Device}: {Temperature}, {Status}",
  "fields": [
    { "name": "Magic",       "type": "u16", "format": "hex", "expect": "0xCAFE" },
    { "name": "Device",      "type": "u8" },
    { "name": "Temperature", "type": "i16", "scale": 0.1, "unit": "°C" },
    { "name": "Status",      "type": "u8",  "enum": { "0": "OK", "1": "Warning", "2": "Error" } },
    { "name": "Flags",       "type": "u8",  "bits": { "0": "armed", "7": "alarm" } },
    { "name": "NameLength",  "type": "u8" },
    { "name": "Name",        "type": "string", "lengthField": "NameLength" },
    { "name": "Extra",       "type": "bytes" }
  ]
}
```

The payload `ca fe 03 00 d7 01 81 05 70 72 6f 62 65 aa bb` sent to UDP port 9000 is shown as:

```text
Example sensor: device 3: 21.5 °C, 1 (Warning)
  Magic: 0xcafe
  Device: 3
  Temperature: 21.5 °C
  Status: 1 (Warning)
  Flags: 129 (armed, alarm)
  NameLength: 5
  Name: probe
  Extra: aabb
```

Try it yourself:

```bash
udptcplistener send 127.0.0.1:9000 -x "CAFE 03 00D7 01 81 05 70726f6265 AABB"
```

## Reference

### Protocol

| Key | Required | Meaning |
|---|---|---|
| `name` | yes | Shown as the protocol name. |
| `transport` | no | `udp`, `tcp` or `any` (default `any`). |
| `ports` | see note | The protocol matches when the source or destination port is in this list. |
| `endian` | no | `big` (default) or `little`. Each field can override it. |
| `summary` | no | Template for the Info column. `{FieldName}` is replaced by that field's value. Without a template, the first three fields are shown. |
| `fields` | yes | Read in order from the start of the payload. |

If `ports` is empty, at least one field must have `expect` (a magic number). Otherwise every
packet would match.

### Field types

| Type | Size | Notes |
|---|---|---|
| `u8` `i8` | 1 | unsigned / signed |
| `u16` `i16` | 2 | |
| `u32` `i32` | 4 | |
| `u64` `i64` | 8 | |
| `f32` `f64` | 4 / 8 | IEEE 754 floating point |
| `bool` | 1 | 0 = false, anything else = true |
| `string` | `length` | UTF-8; trailing zero bytes are removed |
| `cstring` | until a 0 byte | the zero byte is consumed |
| `bytes` | `length` | shown as hex |

For `string` and `bytes`, the size comes from `length` (a fixed number of bytes) or `lengthField`
(the name of an earlier integer field). If neither is given, the field takes the rest of the payload,
which is only allowed for the last field.

### Field options

| Option | Applies to | Meaning |
|---|---|---|
| `endian` | numbers | `big` or `little`, overriding the protocol default. |
| `scale` | numbers | Multiply the value, e.g. `0.1` turns 215 into 21.5. |
| `unit` | numbers | Text appended to the value, e.g. `"°C"`. |
| `format` | integers | `hex` shows `0x00ff` instead of `255`. |
| `enum` | integers | Names for values: `{ "0": "OK", "1": "Error" }`. |
| `bits` | integers | Names for bits (0 = least significant): `{ "0": "armed", "7": "alarm" }`. Shows which are set. |
| `expect` | any | The packet only matches if this field has this value (e.g. `"0xCAFE"`). Use it for magic numbers and version bytes. |

### Matching and errors

- If the payload is shorter than the fields need, or an `expect` value differs, the protocol does
  not match and the packet falls back to the other decoders.
- Leftover bytes after the last field are shown as `(trailing)`.
- Unknown keys are rejected, so a typo such as `"lenght"` is reported instead of silently ignored.
