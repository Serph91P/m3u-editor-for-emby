# Artwork boundary fixtures

These fixtures contain synthetic `example.com` artwork only, not user guide data.

- `core-scalar-poster.xml` is the unmodified XML from the original host boundary
  probe: the programme's scalar icon repeats the qualified poster URL without
  dimensions before the dimensioned icon and standard typed image.
- `core-artwork.json` is actual output of the updated m3u-editor PHP producer on
  branch `fix/issue-1750-artwork-contract` (base
  `0d7bb0a3ccf74170d95e8f85f2e23036dac1cd09`). Its `xml` and `json` members are
  produced by `EpgGenerateController::programmeArtworkXml` and
  `XtreamApiController::epgProgrammeArtwork`, respectively. The latter supplies
  the artwork fields used by `get_simple_data_table`. The probe invokes these
  real methods through reflection with only framework/model bootstrap stubs and
  logo proxying disabled; it is not an HTTP/Laravel integration test.

Synthetic producer inputs are a 500x750 poster, 1280x720 backdrop, 1920x1080
still, an optional 1000x1500 provider poster, and a zero-dimension portrait
control. The wrappers provide only synthetic programme times/channel/title.
The captured JSON artwork fields are not hand-authored consumer assumptions.
Tests also mutate these captured outputs to exercise legacy payloads, URL
rejection, independent poster alternatives and conflicting geometry/roles.

To regenerate, invoke the methods above on the same synthetic programme arrays
(`url`, `type`, `width`, `height`, `orient`, `size`) and store their returned
artwork JSON/XML verbatim. In particular, do not add dimensions to the JSON
fixture unless the real producer emits them. Retain the dimensionless duplicate
in the original XML regression fixture even if the producer changes later.
