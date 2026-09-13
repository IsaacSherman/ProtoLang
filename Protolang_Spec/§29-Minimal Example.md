## 29. Minimal Example

Protobuf schema:

```proto
syntax = "proto3";

message InvoiceItem {
  int64 quantity = 1;
  int64 unit_price_cents = 2;
}

message Invoice {
  repeated InvoiceItem items = 1;
}
```

ProtoLang source:

```protolang
import proto "invoice.proto";

extend InvoiceItem {
    fn line_total_cents() -> int64 {
        return quantity * unit_price_cents;
    }
}

extend Invoice {
    fn total_cents() -> int64 {
        var total: int64 = 0;

        for item in items {
            total = total + item.line_total_cents();
        }

        return total;
    }
}
```

Expected semantic behavior:

- `line_total_cents` returns `quantity * unit_price_cents` with wrapping overflow ([10.1](./§10-Numeric%20Semantics.md#101-integer-overflow)).
- `total_cents` iterates over `items` in protobuf repeated-field order.
- The method performs no I/O and uses no target-language-specific collection helpers.
