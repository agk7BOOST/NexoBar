import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { CopyReference } from "./CopyReference.tsx";
it("copies the complete exact reference and reports local success", async () => {
  const user = userEvent.setup();
  const copy = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  const reference = "01a0f307-e41e-7a00-b994-c23b5d8006f0";
  const view = render(
    <>
      <span>{reference}</span>
      <CopyReference value={reference} />
    </>,
  );
  await user.click(screen.getByRole("button", { name: "Copiar referencia" }));
  expect(copy).toHaveBeenCalledExactlyOnceWith(reference);
  expect(await screen.findByRole("status")).toHaveTextContent("Copiado");
  view.rerender(<CopyReference value="another-reference" />);
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
});
it("keeps the complete displayed value when Clipboard API fails", async () => {
  const user = userEvent.setup();
  vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(
    new Error("Denied"),
  );
  const reference = "complete-reference";
  render(
    <>
      <span>{reference}</span>
      <CopyReference value={reference} />
    </>,
  );
  await user.click(screen.getByRole("button", { name: "Copiar referencia" }));
  expect(await screen.findByRole("status")).toHaveTextContent(
    "Seleccioná la referencia completa y copiala",
  );
  expect(screen.getByText(reference, { exact: true })).toBeVisible();
});
