from typing import Any, IO, Text

from simplejson.scanner import JSONDecodeError as JSONDecodeError
from simplejson.decoder import JSONDecoder as JSONDecoder
from simplejson.encoder import JSONEncoder as JSONEncoder, JSONEncoderForHTML as JSONEncoderForHTML

def dumps(obj: Any, *args: Any, **kwds: Any) -> str: ...
def dump(obj: Any, fp: IO[str], *args: Any, **kwds: Any) -> None: ...
def loads(s: Text, **kwds: Any) -> Any: ...
def load(fp: IO[str], **kwds: Any) -> Any: ...