export function inputClass(hasError: boolean | undefined): string {
  const base =
    "block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1";
  const normal =     "border-gray-300 focus:border-blue-500 focus:ring-blue-500";
  const errored = "border-red-500 focus:border-red-500 focus:ring-red-500";
  return `${base} ${hasError ? errored : normal}`;
}

export function isValidationProblem(
  body: unknown,
): body is { errors: Record<string, string[]> } {
  return (
    !!body &&
    typeof body === "object" &&
    "errors" in (body as Record<string, unknown>)
  );
}
