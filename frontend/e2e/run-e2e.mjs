import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import net from "node:net";
import path from "node:path";

const frontendDirectory = path.dirname(
  path.dirname(fileURLToPath(import.meta.url)),
);
const repositoryRoot = path.dirname(frontendDirectory);
const composeFile = path.join(repositoryRoot, "compose.e2e.yaml");
const composeProject = `nexobar-e2e-${process.pid}-${randomUUID().slice(0, 8)}`;
const composeArguments = ["compose", "-f", composeFile, "-p", composeProject];

function invocation(command, args) {
  if (process.platform !== "win32" || command !== "npm") {
    return { executable: command, args };
  }

  const npmCli = process.env.npm_execpath;
  if (!npmCli) {
    throw new Error(
      "Could not locate the npm CLI. Run the E2E harness through npm run test:e2e.",
    );
  }

  return { executable: process.execPath, args: [npmCli, ...args] };
}

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const childInvocation = invocation(command, args);
    const child = spawn(childInvocation.executable, childInvocation.args, {
      cwd: options.cwd ?? repositoryRoot,
      env: { ...process.env, ...options.env },
      stdio: "inherit",
      windowsHide: true,
    });

    child.once("error", (error) => {
      reject(
        new Error(`Could not start ${options.label ?? command}.`, {
          cause: error,
        }),
      );
    });
    child.once("exit", (code, signal) => {
      if (code === 0) {
        resolve();
        return;
      }

      const result = signal ? `signal ${signal}` : `exit code ${code}`;
      reject(new Error(`${options.label ?? command} failed with ${result}.`));
    });
  });
}

function capture(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const childInvocation = invocation(command, args);
    const child = spawn(childInvocation.executable, childInvocation.args, {
      cwd: options.cwd ?? repositoryRoot,
      env: { ...process.env, ...options.env },
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.once("error", (error) => {
      reject(
        new Error(`Could not start ${options.label ?? command}.`, {
          cause: error,
        }),
      );
    });
    child.once("exit", (code, signal) => {
      if (code === 0) {
        resolve(stdout.trim());
        return;
      }

      const result = signal ? `signal ${signal}` : `exit code ${code}`;
      reject(
        new Error(
          `${options.label ?? command} failed with ${result}. ${stderr.trim()}`,
        ),
      );
    });
  });
}

function assertPortAvailable(port) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: "127.0.0.1", port });
    let settled = false;

    function finish(callback) {
      if (settled) {
        return;
      }

      settled = true;
      socket.destroy();
      callback();
    }

    socket.setTimeout(750);
    socket.once("connect", () => {
      finish(() =>
        reject(
          new Error(
            `Port ${port} is already in use. E2E will not reuse or stop the existing process.`,
          ),
        ),
      );
    });
    socket.once("timeout", () => finish(resolve));
    socket.once("error", (error) => {
      if (error.code === "ECONNREFUSED" || error.code === "EHOSTUNREACH") {
        finish(resolve);
        return;
      }

      finish(() => reject(error));
    });
  });
}

function parsePostgresPort(publishedPort) {
  const match = /^127\.0\.0\.1:(\d+)$/.exec(publishedPort);

  if (!match) {
    throw new Error(
      `Could not parse the E2E PostgreSQL published port: ${publishedPort}`,
    );
  }

  return match[1];
}

async function main() {
  await assertPortAvailable(5028);
  await assertPortAvailable(5173);

  let executionError;
  let composeAttempted = false;

  try {
    composeAttempted = true;
    console.log(`[e2e] Starting isolated PostgreSQL (${composeProject})...`);
    await run("docker", [...composeArguments, "up", "-d", "--wait"], {
      label: "E2E PostgreSQL startup",
    });

    const publishedPort = await capture(
      "docker",
      [...composeArguments, "port", "postgres", "5432"],
      { label: "E2E PostgreSQL port discovery" },
    );
    const postgresPort = parsePostgresPort(publishedPort);
    const connectionString =
      `Host=127.0.0.1;Port=${postgresPort};Database=nexobar_e2e;` +
      "Username=nexobar_e2e;Password=nexobar_e2e_password;Include Error Detail=true";
    const e2eEnvironment = {
      NEXOBAR_E2E_CONNECTION_STRING: connectionString,
    };

    console.log("[e2e] Applying compiled EF Core migrations...");
    await run(
      "dotnet",
      [
        "run",
        "--project",
        path.join(
          repositoryRoot,
          "backend",
          "tests",
          "NexoBar.E2E.DatabaseSetup",
          "NexoBar.E2E.DatabaseSetup.csproj",
        ),
        "--no-launch-profile",
      ],
      { env: e2eEnvironment, label: "E2E database setup" },
    );

    console.log("[e2e] Running Chromium scenarios...");
    await run("npm", ["run", "test:e2e:playwright"], {
      cwd: frontendDirectory,
      env: e2eEnvironment,
      label: "Playwright E2E",
    });
  } catch (error) {
    executionError = error;
  } finally {
    if (composeAttempted) {
      console.log(`[e2e] Removing isolated PostgreSQL (${composeProject})...`);
      try {
        await run(
          "docker",
          [...composeArguments, "down", "--volumes", "--remove-orphans"],
          { label: "E2E PostgreSQL cleanup" },
        );
      } catch (cleanupError) {
        executionError = executionError
          ? new AggregateError(
              [executionError, cleanupError],
              "E2E failed and PostgreSQL cleanup also failed.",
            )
          : cleanupError;
      }
    }
  }

  if (executionError) {
    throw executionError;
  }
}

main().catch((error) => {
  console.error(`[e2e] ${error.stack ?? error}`);
  process.exitCode = 1;
});
