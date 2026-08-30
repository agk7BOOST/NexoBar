import { describe, expect, it } from "vitest";
import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";

describe("canonicalizeConfirmationInstruction", () => {
  it("normaliza saltos de línea, recorta el exterior y preserva el contenido", () => {
    expect(
      canonicalizeConfirmationInstruction(
        " \t SIN  cebolla!\r\nnota\rfinal  \n",
      ),
    ).toBe("SIN  cebolla!\nnota\nfinal");
  });

  it.each(["", "   ", "\t\r\n "])(
    "convierte texto blank en ausencia: %j",
    (value) => {
      expect(canonicalizeConfirmationInstruction(value)).toBeNull();
    },
  );
});
