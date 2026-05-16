import { createHash } from "node:crypto";

export function getDownloadKey(path: string): string {
    var input = `${path}_${process.env.FRONTEND_BACKEND_API_KEY}`;
    return createHash('sha256').update(input).digest('hex');
}

export function verifyDownloadKey(downloadKey: string | null, path: string): boolean {
    var input = `${path}_${process.env.FRONTEND_BACKEND_API_KEY}`;
    var hash = createHash('sha256').update(input).digest('hex');
    return downloadKey == hash;
}