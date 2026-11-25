# A stand-in for the bucket: stores objects in a folder, checks no signatures. For testing DLF only.
# Usage: python fakes3.py <folder> <port>. An object /bucket/a/b is the file bucket__a__b in the folder.
import os, sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

ROOT = sys.argv[1]

class Handler(BaseHTTPRequestHandler):
    def _path(self):
        return os.path.join(ROOT, urlparse(self.path).path.lstrip('/').replace('/', '__'))

    def do_GET(self):
        url = urlparse(self.path)
        query = parse_qs(url.query)
        if 'list-type' in query:
            # ListObjectsV2: the keys of the bucket that start with the prefix
            bucket = url.path.strip('/')
            start = bucket + '__' + query.get('prefix', [''])[0].replace('/', '__')
            keys = [n[len(bucket) + 2:].replace('__', '/') for n in sorted(os.listdir(ROOT)) if n.startswith(start)]
            data = ('<ListBucketResult>' + ''.join('<Contents><Key>%s</Key></Contents>' % k for k in keys) + '</ListBucketResult>').encode()
            self.send_response(200); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data); return
        p = self._path()
        if not os.path.isfile(p):
            self.send_response(404); self.end_headers(); self.wfile.write(b'<Error>NoSuchKey</Error>'); return
        data = open(p, 'rb').read()
        self.send_response(200); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data)

    def do_PUT(self):
        data = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        if self.headers.get('If-None-Match') == '*' and os.path.isfile(self._path()):
            self.send_response(412); self.send_header('Content-Length', '0'); self.end_headers(); return
        open(self._path(), 'wb').write(data)
        self.send_response(200); self.send_header('Content-Length', '0'); self.end_headers()

    def do_DELETE(self):
        if os.path.isfile(self._path()):
            os.remove(self._path())
        self.send_response(204); self.end_headers()

    def log_message(self, *a):
        pass

HTTPServer(('127.0.0.1', int(sys.argv[2])), Handler).serve_forever()
