## 17. Virtual and Override Semantics

ProtoLang parses `virtual`, but the implemented backends reject virtual methods.

### 17.1 Design Principle

Current Status:

- `virtual` is accepted by the parser and carried in the IR.
- The C# and C++ backends reject virtual methods at compile time because portable override semantics
  are not defined.
- `virtual` does not define a ProtoLang subclass model.
- ProtoLang source cannot declare subclasses.
- Generated protobuf message subclasses are forbidden as a portability strategy.

```protolang
extend DetectorReading {
    virtual fn overridable_count_rate() -> int64 {
        return counts / live_time on_zero fail;
    }
}
```

Possible future shape. Overridable functions cannot behave like classical virtual functions:
protobuf compiles into sealed classes in C# so inheritance is impossible. They would need something
like this by necessity:

```csharp
	public partial class DetectorReading{
	public delegate double DetectorReadingCountRateOverride();  //We can also just use Func<double> here.
	public static DetectorReadingCountRateOverride CountRateOverride {get;set;} //Note: Proto files are nullable agnostic
	public double RealTime {get;set;}
	public double CountRate(){
		if(CountRateOverride != null)
			return CountRateOverride();
		else
			return counts / RealTime;
	}
}
```

`That said, I'm really not convinced they're a good idea at all. Again, we're writing this because *we don't want to have more than 1 source of truth for behavior*.  Virtual functions are antithetical to that.  But they might be a necessary workaround for some people in some scenarios- I just don't know what they might be. ~IS`

### 17.2 Backend Strategies

Possible C# strategies:

- Generate a static, settable delegate field in the class.  
- Generate partial method hooks.
- Generate an adapter/wrapper class.

Possible C++ strategies:

- Generate free functions plus overridable policy objects.
- Use protobuf generator insertion points where appropriate.
- Generate wrapper/adaptor classes rather than subclass protobuf messages.

Possible Python strategies:

- Generate regular methods.
- Generate mixins or monkey-patch registration helpers.
- Generate wrapper classes.

Open Questions:

- Is `virtual` part of version 1?  `I think yes.  But it's low on the list. We're doing this because we DON'T want to write functions for every language. ~IS`
- Must all backends support virtual behavior, or may some reject it? `If we're going to do it... we should do it for every language. All languages should be able to support something like this, even if it's not explicitly supported. ~IS`
- Should there be a portable override registration mechanism? `Out of scope for v1. ~IS`
