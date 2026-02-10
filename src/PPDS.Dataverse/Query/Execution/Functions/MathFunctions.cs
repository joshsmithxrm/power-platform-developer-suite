using System;
using System.Globalization;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL math functions evaluated client-side.
/// All functions propagate NULL (return NULL if any argument is NULL).
/// </summary>
public static class MathFunctions
{
    /// <summary>
    /// Registers all math functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("ABS", new AbsFunction());
        registry.Register("CEILING", new CeilingFunction());
        registry.Register("FLOOR", new FloorFunction());
        registry.Register("ROUND", new RoundFunction());
        registry.Register("POWER", new PowerFunction());
        registry.Register("LOG", new LogFunction());
        registry.Register("LOG10", new Log10Function());
        registry.Register("SQRT", new SqrtFunction());
        registry.Register("EXP", new ExpFunction());
        registry.Register("SIN", new SinFunction());
        registry.Register("COS", new CosFunction());
        registry.Register("TAN", new TanFunction());
        registry.Register("ASIN", new AsinFunction());
        registry.Register("ACOS", new AcosFunction());
        registry.Register("ATAN", new AtanFunction());
        registry.Register("ATN2", new Atn2Function());
        registry.Register("ATAN2", new Atn2Function());
        registry.Register("DEGREES", new DegreesFunction());
        registry.Register("RADIANS", new RadiansFunction());
        registry.Register("RAND", new RandFunction());
        registry.Register("PI", new PiFunction());
        registry.Register("SQUARE", new SquareFunction());
        registry.Register("SIGN", new SignFunction());
    }

    /// <summary>
    /// Helper: converts an argument to double.
    /// </summary>
    private static double ToDouble(object value)
    {
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns NULL if any argument is NULL.
    /// </summary>
    private static bool HasNull(object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is null) return true;
        }
        return false;
    }

    // ── ABS ────────────────────────────────────────────────────────────
    /// <summary>
    /// ABS(value) - returns the absolute value.
    /// </summary>
    private sealed class AbsFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            return value switch
            {
                int i => Math.Abs(i),
                long l => Math.Abs(l),
                decimal d => Math.Abs(d),
                double dbl => Math.Abs(dbl),
                float f => Math.Abs(f),
                _ => Math.Abs(ToDouble(value))
            };
        }
    }

    // ── CEILING ────────────────────────────────────────────────────────
    /// <summary>
    /// CEILING(value) - returns the smallest integer >= value.
    /// </summary>
    private sealed class CeilingFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            return value switch
            {
                int i => i,
                long l => l,
                decimal d => Math.Ceiling(d),
                double dbl => Math.Ceiling(dbl),
                float f => Math.Ceiling(f),
                _ => Math.Ceiling(ToDouble(value))
            };
        }
    }

    // ── FLOOR ──────────────────────────────────────────────────────────
    /// <summary>
    /// FLOOR(value) - returns the largest integer &lt;= value.
    /// </summary>
    private sealed class FloorFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            return value switch
            {
                int i => i,
                long l => l,
                decimal d => Math.Floor(d),
                double dbl => Math.Floor(dbl),
                float f => Math.Floor(f),
                _ => Math.Floor(ToDouble(value))
            };
        }
    }

    // ── ROUND ──────────────────────────────────────────────────────────
    /// <summary>
    /// ROUND(value, length) - rounds a value to specified number of decimal places.
    /// </summary>
    private sealed class RoundFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            var length = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
            return value switch
            {
                decimal d => Math.Round(d, length, MidpointRounding.AwayFromZero),
                double dbl => Math.Round(dbl, length, MidpointRounding.AwayFromZero),
                float f => Math.Round(f, length, MidpointRounding.AwayFromZero),
                _ => Math.Round(ToDouble(value), length, MidpointRounding.AwayFromZero)
            };
        }
    }

    // ── POWER ──────────────────────────────────────────────────────────
    /// <summary>
    /// POWER(base, exponent) - returns base raised to exponent.
    /// </summary>
    private sealed class PowerFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Pow(ToDouble(args[0]!), ToDouble(args[1]!));
        }
    }

    // ── LOG ─────────────────────────────────────────────────────────────
    /// <summary>
    /// LOG(value) - natural logarithm. LOG(value, base) - logarithm with specified base.
    /// </summary>
    private sealed class LogFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = ToDouble(args[0]!);
            if (args.Length == 2)
            {
                var logBase = ToDouble(args[1]!);
                return Math.Log(value, logBase);
            }
            return Math.Log(value);
        }
    }

    // ── LOG10 ───────────────────────────────────────────────────────────
    /// <summary>
    /// LOG10(value) - base-10 logarithm.
    /// </summary>
    private sealed class Log10Function : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Log10(ToDouble(args[0]!));
        }
    }

    // ── SQRT ────────────────────────────────────────────────────────────
    /// <summary>
    /// SQRT(value) - square root.
    /// </summary>
    private sealed class SqrtFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Sqrt(ToDouble(args[0]!));
        }
    }

    // ── EXP ─────────────────────────────────────────────────────────────
    /// <summary>
    /// EXP(value) - returns e raised to the specified power.
    /// </summary>
    private sealed class ExpFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Exp(ToDouble(args[0]!));
        }
    }

    // ── SIN ─────────────────────────────────────────────────────────────
    /// <summary>
    /// SIN(value) - returns the sine of an angle in radians.
    /// </summary>
    private sealed class SinFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Sin(ToDouble(args[0]!));
        }
    }

    // ── COS ─────────────────────────────────────────────────────────────
    /// <summary>
    /// COS(value) - returns the cosine of an angle in radians.
    /// </summary>
    private sealed class CosFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Cos(ToDouble(args[0]!));
        }
    }

    // ── TAN ─────────────────────────────────────────────────────────────
    /// <summary>
    /// TAN(value) - returns the tangent of an angle in radians.
    /// </summary>
    private sealed class TanFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Tan(ToDouble(args[0]!));
        }
    }

    // ── ASIN ────────────────────────────────────────────────────────────
    /// <summary>
    /// ASIN(value) - returns the arc sine in radians.
    /// </summary>
    private sealed class AsinFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Asin(ToDouble(args[0]!));
        }
    }

    // ── ACOS ────────────────────────────────────────────────────────────
    /// <summary>
    /// ACOS(value) - returns the arc cosine in radians.
    /// </summary>
    private sealed class AcosFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Acos(ToDouble(args[0]!));
        }
    }

    // ── ATAN ────────────────────────────────────────────────────────────
    /// <summary>
    /// ATAN(value) - returns the arc tangent in radians.
    /// </summary>
    private sealed class AtanFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Atan(ToDouble(args[0]!));
        }
    }

    // ── ATN2 / ATAN2 ───────────────────────────────────────────────────
    /// <summary>
    /// ATN2(y, x) / ATAN2(y, x) - returns the angle in radians between the positive x-axis
    /// and the point (x, y).
    /// </summary>
    private sealed class Atn2Function : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return Math.Atan2(ToDouble(args[0]!), ToDouble(args[1]!));
        }
    }

    // ── DEGREES ─────────────────────────────────────────────────────────
    /// <summary>
    /// DEGREES(radians) - converts radians to degrees.
    /// </summary>
    private sealed class DegreesFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return ToDouble(args[0]!) * (180.0 / Math.PI);
        }
    }

    // ── RADIANS ─────────────────────────────────────────────────────────
    /// <summary>
    /// RADIANS(degrees) - converts degrees to radians.
    /// </summary>
    private sealed class RadiansFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return ToDouble(args[0]!) * (Math.PI / 180.0);
        }
    }

    // ── RAND ────────────────────────────────────────────────────────────
    /// <summary>
    /// RAND([seed]) - returns a pseudo-random float between 0 and 1.
    /// </summary>
    private sealed class RandFunction : IScalarFunction
    {
        public int MinArgs => 0;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            Random rng;
            if (args.Length >= 1 && args[0] is not null)
            {
                var seed = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
                rng = new Random(seed);
            }
            else
            {
                rng = new Random();
            }
            return rng.NextDouble();
        }
    }

    // ── PI ──────────────────────────────────────────────────────────────
    /// <summary>
    /// PI() - returns the constant value of PI.
    /// </summary>
    private sealed class PiFunction : IScalarFunction
    {
        public int MinArgs => 0;
        public int MaxArgs => 0;

        public object? Execute(object?[] args)
        {
            return Math.PI;
        }
    }

    // ── SQUARE ──────────────────────────────────────────────────────────
    /// <summary>
    /// SQUARE(value) - returns the square of the specified float value.
    /// </summary>
    private sealed class SquareFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = ToDouble(args[0]!);
            return value * value;
        }
    }

    // ── SIGN ────────────────────────────────────────────────────────────
    /// <summary>
    /// SIGN(value) - returns +1, 0, or -1 indicating the sign of the value.
    /// </summary>
    private sealed class SignFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            return value switch
            {
                int i => Math.Sign(i),
                long l => Math.Sign(l),
                decimal d => Math.Sign(d),
                double dbl => Math.Sign(dbl),
                float f => Math.Sign(f),
                _ => Math.Sign(ToDouble(value))
            };
        }
    }
}
