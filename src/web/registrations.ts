import { createRpcClient } from "../common/rpc/rpc-client";
import { RPC_TIMEOUT_MS, type HostAPI } from "../common/types";
import Router from "./router";

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };

const vscode = acquireVsCodeApi();

/** The one place the webview talks to the extension host. */
export const hostApi: HostAPI = createRpcClient((msg) => vscode.postMessage(msg), RPC_TIMEOUT_MS);

export const router = new Router();
