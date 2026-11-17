# OME-NGFF JSON schemas

Unmodified copies of the OME-NGFF image schemas. `OmeZarrTests` validates the metadata that
`OmeZarrGenerator` writes against them (with JsonSchema.Net, a test-only dependency). They were
downloaded on 2026-11-17 and are byte-identical to the files at tag `0.5.2` of
[ome/ngff](https://github.com/ome/ngff).

| File | Source |
|---|---|
| `ngff-0.4/image.schema` | https://ngff.openmicroscopy.org/0.4/schemas/image.schema |
| `ngff-0.4/strict_image.schema` | https://ngff.openmicroscopy.org/0.4/schemas/strict_image.schema |
| `ngff-0.5/image.schema` | https://ngff.openmicroscopy.org/0.5/schemas/image.schema |
| `ngff-0.5/strict_image.schema` | https://ngff.openmicroscopy.org/0.5/schemas/strict_image.schema |
| `ngff-0.5/_version.schema` | https://ngff.openmicroscopy.org/0.5/schemas/_version.schema |

NGFF 0.4 has no `_version.schema`. `strict_image.schema` refers to `image.schema`, and the 0.5
`image.schema` refers to `_version.schema`, by their `$id` URLs, so the tests register the schemas in
that order and never download anything.

## License

The schema files have no license header of their own. The OME-NGFF specification is
Copyright © 2020-2026 OME® (University of Dundee). The
[LICENSE.md](https://github.com/ome/ngff/blob/main/LICENSE.md) of the repository that publishes them
licenses its reports under the
[W3C Software and Document License](https://www.w3.org/Consortium/Legal/2015/copyright-software-and-document)
and its software under the [Apache 2.0 License](https://www.apache.org/licenses/LICENSE-2.0).
