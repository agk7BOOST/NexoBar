export function canonicalizeConfirmationInstruction(
  value: string,
): string | null {
  const canonical = value.replace(/\r\n?/g, "\n").trim();

  return canonical.length === 0 ? null : canonical;
}
