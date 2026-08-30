import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";

export interface CompositionLine {
  draftLineId: string;
  productId: string;
  quantity: number;
  instruction: string;
}

export function hasDuplicateCompositionLines(
  lines: CompositionLine[],
): boolean {
  const seenByProduct = new Map<string, Set<string | null>>();

  for (const line of lines) {
    const instruction = canonicalizeConfirmationInstruction(line.instruction);
    const seenInstructions = seenByProduct.get(line.productId) ?? new Set();

    if (seenInstructions.has(instruction)) {
      return true;
    }

    seenInstructions.add(instruction);
    seenByProduct.set(line.productId, seenInstructions);
  }

  return false;
}
