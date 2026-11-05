import { browser } from 'wxt/browser';
import { checkReply, explainNativeError, HOST_NAME, PROTOCOL_VERSION, type NativeReply, type NativeRequest } from './protocol';

/** Asks NudgeJudge. Never throws: a failure comes back as an error reply that says what to do. */
export async function sendNative(request: NativeRequest): Promise<NativeReply> {
  try {
    return checkReply(await browser.runtime.sendNativeMessage(HOST_NAME, request));
  } catch (error) {
    return { v: PROTOCOL_VERSION, ok: false, error: explainNativeError(error) };
  }
}
