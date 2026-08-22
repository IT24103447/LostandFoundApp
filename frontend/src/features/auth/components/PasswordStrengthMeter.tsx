import { useMemo } from "react";

type Strength = 0 | 1 | 2 | 3 | 4;

const STRENGTH_CONFIG: Record<
  Strength,
  { label: string; color: string; bar: string }
> = {
  0: { label: "", color: "text-gray-400", bar: "bg-gray-200" },
  1: { label: "Weak", color: "text-red-600", bar: "bg-red-500" },
  2: { label: "Fair", color: "text-orange-500", bar: "bg-orange-400" },
  3: { label: "Good", color: "text-yellow-600", bar: "bg-yellow-400" },
  4: { label: "Strong", color: "text-emerald-600", bar: "bg-emerald-500" },
};

type PasswordStrengthMeterProps = {
  password: string;
};

export function PasswordStrengthMeter({ password }: PasswordStrengthMeterProps) {
  const strength = useMemo<Strength>(() => {
    if (!password) return 0;
    let score = 0;
    if (password.length >= 8) score++;
    if (/[A-Z]/.test(password)) score++;
    if (/[a-z]/.test(password)) score++;
    if (/[0-9]/.test(password)) score++;
    return score as Strength;
  }, [password]);

  const config = STRENGTH_CONFIG[strength];

  if (!password) return null;

  return (
    <div className="mt-1.5 space-y-1">
      <div className="flex gap-1">
        {[1, 2, 3, 4].map((level) => (
          <div
            key={level}
            className={`h-1 flex-1 rounded-full transition-colors duration-300 ${
              level <= strength ? config.bar : "bg-gray-200"
            }`}
          />
        ))}
      </div>
      {config.label && (
        <p className={`text-xs font-medium ${config.color}`}>
          {config.label}
        </p>
      )}
    </div>
  );
}
