import type { Product } from "../catalog/catalogClient.ts";
import { canonicalizeConfirmationInstruction } from "./confirmationInstruction.ts";
import type { CompositionLine } from "./composition.ts";

interface CompositionLineEditorProps {
  line: CompositionLine;
  lineNumber: number;
  product: Product;
  isLocked: boolean;
  shouldFocusInstruction: boolean;
  onInstructionChange: (draftLineId: string, instruction: string) => void;
  onIncrease: (draftLineId: string) => void;
  onDecrease: (draftLineId: string) => void;
  onRemove: (draftLineId: string) => void;
}

export function CompositionLineEditor({
  line,
  lineNumber,
  product,
  isLocked,
  shouldFocusInstruction,
  onInstructionChange,
  onIncrease,
  onDecrease,
  onRemove,
}: CompositionLineEditorProps) {
  const canonicalInstruction = canonicalizeConfirmationInstruction(
    line.instruction,
  );

  return (
    <tr
      aria-label={`${product.operationalName}, línea de Composición ${lineNumber}, ${canonicalInstruction ?? "sin instrucción"}`}
    >
      <td>{product.operationalName}</td>
      <td>{product.price}</td>
      <td aria-label={`Cantidad de ${product.operationalName}`}>
        {line.quantity}
      </td>
      <td>
        <label
          className="visually-hidden"
          htmlFor={`instruction-${line.draftLineId}`}
        >
          Instrucción para {product.operationalName}, línea {lineNumber}
        </label>
        <textarea
          id={`instruction-${line.draftLineId}`}
          className="composition-instruction"
          value={line.instruction}
          onChange={(event) =>
            onInstructionChange(line.draftLineId, event.target.value)
          }
          disabled={isLocked}
          placeholder="Sin instrucción"
          rows={2}
          autoFocus={shouldFocusInstruction}
        />
      </td>
      <td>
        <div className="composition-actions">
          <button
            type="button"
            onClick={() => onIncrease(line.draftLineId)}
            disabled={isLocked}
            aria-label={`Aumentar cantidad de ${product.operationalName}, línea ${lineNumber}`}
          >
            +1
          </button>
          <button
            className="secondary-button"
            type="button"
            onClick={() => onDecrease(line.draftLineId)}
            disabled={isLocked}
            aria-label={`Disminuir cantidad de ${product.operationalName}, línea ${lineNumber}`}
          >
            −1
          </button>
          <button
            className="secondary-button"
            type="button"
            onClick={() => onRemove(line.draftLineId)}
            disabled={isLocked}
            aria-label={`Retirar ${product.operationalName}, línea ${lineNumber}, de la composición`}
          >
            Retirar
          </button>
        </div>
      </td>
    </tr>
  );
}
